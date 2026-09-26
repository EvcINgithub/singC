using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using singC.Helpers;
using singC.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace singC.Pages;

public sealed partial class NetworkTestPage : Page
{
    private readonly NetworkTestService _testService = new();
    private CancellationTokenSource? _testCts;
    private bool _isTesting;
    private bool _historyDialogOpen;
    private NetworkTestOptions? _runOptions;
    private bool _stability;
    private DateTime _startedAt;
    private string _completion = "尚未测试";

    public ObservableCollection<NetworkTestResult> WebsiteResults { get; } = new();
    public ObservableCollection<NetworkTestResult> DetailResults { get; } = new();
    public ObservableCollection<NetworkTestRunSummary> History { get; } = new();
    private readonly List<NetworkTestResult> _results = new();

    public NetworkTestPage()
    {
        InitializeComponent();
        TestUrlBox.Text = AppSettings.Get(AppSettings.NetworkTestUrlKey) ?? NetworkTestOptions.DefaultTarget;
        ExitIpBox.Text = AppSettings.Get(AppSettings.NetworkTestExitIpUrlKey) ?? NetworkTestOptions.DefaultExitIp;
        TimeoutBox.Value = int.TryParse(AppSettings.Get(AppSettings.NetworkTestTimeoutKey), out int timeout)
            ? Math.Clamp(timeout, 1, 30) : 5;
        CommonSitesCheckBox.IsChecked = AppSettings.Get(AppSettings.NetworkTestCommonSitesKey) != "False";
        CheckExitIpBox.IsChecked = AppSettings.Get(AppSettings.NetworkTestCheckExitIpKey) != "False";
        LoadHistory();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _testCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isTesting && sender is Button { Tag: string url }) TestUrlBox.Text = url;
    }

    private async void RunDiagnosticsButton_Click(object sender, RoutedEventArgs e) => await RunTestsAsync(false);
    private async void RunStabilityButton_Click(object sender, RoutedEventArgs e) => await RunTestsAsync(true);

    private async Task RunTestsAsync(bool stability)
    {
        if (_isTesting) return;
        if (!TryReadOptions(stability, out var options, out var error))
        {
            SummaryTextBlock.Text = error;
            return;
        }

        _runOptions = options;
        _stability = stability;
        _startedAt = DateTime.Now;
        _completion = "测试中";
        _results.Clear();
        WebsiteResults.Clear();
        DetailResults.Clear();
        DetailsExpander.IsExpanded = false;
        ResultHeading.Text = stability ? "连续请求结果" : "网站连通性";
        SummaryTextBlock.Text = stability ? "正在连续发送 10 次请求…" : "正在检查网站和连接…";
        LastRunTextBlock.Text = $"开始于 {_startedAt:HH:mm:ss}";
        StorageMessageText.Text = "";
        EmptyResultsText.Visibility = Visibility.Visible;
        EmptyResultsText.Text = "正在等待网站响应，结果将逐项显示…";
        SetTestingState(true);
        using var cts = new CancellationTokenSource();
        _testCts = cts;
        try
        {
            SaveTargets(options!);
            var progress = new ResultProgress(result =>
            {
                _results.Add(result);
                if (result.Kind == NetworkTestKind.Http)
                {
                    WebsiteResults.Add(result);
                    EmptyResultsText.Visibility = Visibility.Collapsed;
                }
                else DetailResults.Add(result);
                SummaryTextBlock.Text = "测试中 · " + NetworkTestService.Summarize(_results, stability);
            });
            if (stability)
                await _testService.RunStabilityAsync(options!, cts.Token, progress);
            else
                await _testService.RunDiagnosticsAsync(options!, SingBoxService.Instance.IsRunning,
                    AppSettings.Get(AppSettings.PathKey.ConfigPathKey), cts.Token, progress);
            _completion = "已完成";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _completion = "已取消，已完成的结果已保留";
        }
        catch (Exception ex)
        {
            _completion = $"测试未完成：{ex.Message}";
        }
        finally
        {
            _testCts = null;
            SetTestingState(false);
            SummaryTextBlock.Text = _completion + "\n" + NetworkTestService.Summarize(_results, stability);
            LastRunTextBlock.Text = $"最近测试：{_startedAt:HH:mm:ss}";
            if (WebsiteResults.Count == 0) EmptyResultsText.Text = "本次尚无网站测试结果，可以重新开始诊断。";
            if (_results.Any(r => r.Kind is NetworkTestKind.Dns or NetworkTestKind.Tcp && r.Status == NetworkTestStatus.Failed))
                DetailsExpander.IsExpanded = true;
            if (_results.Count > 0) AddHistory();
        }
    }

    // Service continuations preserve the UI context. Synchronous reporting avoids late
    // Progress<T> callbacks modifying the next run after completion or cancellation.
    private sealed class ResultProgress(Action<NetworkTestResult> report) : IProgress<NetworkTestResult>
    {
        public void Report(NetworkTestResult value) => report(value);
    }

    private bool TryReadOptions(bool stability, out NetworkTestOptions? options, out string error)
    {
        options = null;
        error = "";
        if (!NetworkTestOptions.TryParseUrl(TestUrlBox.Text, out var target))
        {
            error = "请输入完整的 HTTP/HTTPS 网址，例如 https://github.com/，网址中不能包含账号密码。";
            return false;
        }
        Uri? exitIp = null;
        if (!stability && CheckExitIpBox.IsChecked == true
            && !NetworkTestOptions.TryParseUrl(ExitIpBox.Text, out exitIp))
        {
            error = "出口 IP 服务地址无效，请在高级选项中修改或关闭出口查询。";
            AdvancedExpander.IsExpanded = true;
            return false;
        }
        if (!double.IsFinite(TimeoutBox.Value) || TimeoutBox.Value % 1 != 0 || TimeoutBox.Value is < 1 or > 30)
        {
            error = "单项超时必须是 1 到 30 秒的整数。";
            AdvancedExpander.IsExpanded = true;
            return false;
        }
        options = new(target!, exitIp, (int)TimeoutBox.Value, CommonSitesCheckBox.IsChecked == true);
        return true;
    }

    private void SaveTargets(NetworkTestOptions options)
    {
        try
        {
            AppSettings.Set(AppSettings.NetworkTestUrlKey, options.Target.AbsoluteUri);
            AppSettings.Set(AppSettings.NetworkTestExitIpUrlKey, ExitIpBox.Text.Trim());
            AppSettings.Set(AppSettings.NetworkTestTimeoutKey, options.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            AppSettings.Set(AppSettings.NetworkTestCommonSitesKey, (CommonSitesCheckBox.IsChecked == true).ToString());
            AppSettings.Set(AppSettings.NetworkTestCheckExitIpKey, (CheckExitIpBox.IsChecked == true).ToString());
        }
        catch (Exception ex) { StorageMessageText.Text = $"测试参数未能保存：{ex.Message}"; }
    }

    private void LoadHistory()
    {
        try
        {
            var history = JsonSerializer.Deserialize<List<NetworkTestRunSummary>>(
                AppSettings.Get(AppSettings.NetworkTestHistoryKey) ?? "[]");
            foreach (var item in history?.Take(10) ?? Enumerable.Empty<NetworkTestRunSummary>()) History.Add(item);
        }
        catch { StorageMessageText.Text = "历史记录读取失败，仍可开始新的测试。"; }
    }

    private void AddHistory()
    {
        var summary = new NetworkTestRunSummary
        {
            Timestamp = _startedAt,
            Mode = (_stability ? "连续请求" : "一键诊断") + (_completion == "已完成" ? "" : "（未完成）"),
            Passed = _results.Count(r => r.Status == NetworkTestStatus.Success),
            Warnings = _results.Count(r => r.Status == NetworkTestStatus.Warning),
            Failed = _results.Count(r => r.Status == NetworkTestStatus.Failed),
            FailureText = string.Join("；", _results.Where(r => r.Status != NetworkTestStatus.Success)
                .Select(r => $"{r.Name}：{r.Detail}").Take(3)),
            Report = BuildReport()
        };
        History.Insert(0, summary);
        while (History.Count > 10) History.RemoveAt(History.Count - 1);
        try { AppSettings.Set(AppSettings.NetworkTestHistoryKey, JsonSerializer.Serialize(History)); }
        catch (Exception ex) { StorageMessageText.Text = $"本次历史记录未能保存：{ex.Message}"; }
    }

    private string BuildReport()
    {
        if (_runOptions == null) return "";
        return string.Join(Environment.NewLine, new[]
        {
            "singC 网络诊断报告",
            $"开始时间：{_startedAt:yyyy-MM-dd HH:mm:ss}",
            $"模式：{(_stability ? "连续请求" : "一键诊断")} · {_completion}",
            $"目标：{_runOptions.Target}",
            $"单项超时：{_runOptions.TimeoutSeconds} 秒",
            _stability ? $"计划请求：{NetworkTestService.StabilityAttempts} 次"
                : $"网站列表：{string.Join("，", _runOptions.HttpTargets)}",
            !_stability && _runOptions.ExitIp != null ? $"出口查询：{_runOptions.ExitIp}" : "未执行出口查询",
            "HTTP 跟随系统代理、TUN 和路由；DNS/TCP 使用系统网络，不能单独证明代理生效。",
            NetworkTestService.Summarize(_results, _stability),
            ""
        }.Concat(_results.Select(r => r.ReportLine)));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isTesting) return;
        SummaryTextBlock.Text = "正在取消，已完成的结果将保留…";
        CancelButton.IsEnabled = false;
        _testCts?.Cancel();
    }

    private void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_results.Count == 0) return;
        try
        {
            var package = new DataPackage();
            package.SetText(BuildReport());
            Clipboard.SetContent(package);
            StorageMessageText.Text = "报告已复制。";
        }
        catch (Exception ex) { StorageMessageText.Text = $"复制失败：{ex.Message}"; }
    }

    private async void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_historyDialogOpen || e.ClickedItem is not NetworkTestRunSummary item) return;
        _historyDialogOpen = true;
        var report = string.IsNullOrWhiteSpace(item.Report) ? item.DisplayText + "\n" + item.FailureDisplayText : item.Report;
        var dialog = new ContentDialog
        {
            Title = "历史测试报告",
            Content = new ScrollViewer
            {
                MaxHeight = 440,
                Content = new TextBlock { Text = report, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        try { await dialog.ShowAsync(); }
        catch (Exception ex) { StorageMessageText.Text = $"无法打开历史报告：{ex.Message}"; }
        finally { _historyDialogOpen = false; }
    }

    private void SetTestingState(bool testing)
    {
        _isTesting = testing;
        RunDiagnosticsButton.IsEnabled = !testing;
        RunStabilityButton.IsEnabled = !testing;
        CancelButton.IsEnabled = testing;
        CancelButton.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;
        TestUrlBox.IsEnabled = !testing;
        CommonSitesCheckBox.IsEnabled = !testing;
        foreach (var button in PresetPanel.Children.OfType<Button>()) button.IsEnabled = !testing;
        AdvancedExpander.IsEnabled = !testing;
        TestProgress.IsActive = testing;
        TestProgress.Visibility = testing ? Visibility.Visible : Visibility.Collapsed;
        CopyReportButton.IsEnabled = !testing && _results.Count > 0;
    }
}
