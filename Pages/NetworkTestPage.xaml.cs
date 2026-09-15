using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using singC.Helpers;
using singC.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace singC.Pages;

public sealed partial class NetworkTestPage : Page
{
    private const string DefaultTestUrl = "https://www.baidu.com/";
    private const string DefaultHost = "www.baidu.com";
    private const int DefaultPort = 443;
    private const string DefaultExitIpUrl = "https://api.ipify.org?format=json";

    private readonly NetworkTestService _testService = new();
    private CancellationTokenSource? _testCts;
    private bool _isTesting;

    public ObservableCollection<NetworkTestResult> Results { get; } = new();
    public ObservableCollection<NetworkTestRunSummary> History { get; } = new();

    public NetworkTestPage()
    {
        InitializeComponent();
        LoadTargets();
        LoadHistory();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _testCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private void LoadTargets()
    {
        TestUrlBox.Text = AppSettings.Get(AppSettings.NetworkTestUrlKey) ?? DefaultTestUrl;
        HostBox.Text = AppSettings.Get(AppSettings.NetworkTestHostKey) ?? DefaultHost;
        ExitIpBox.Text = AppSettings.Get(AppSettings.NetworkTestExitIpUrlKey) ?? DefaultExitIpUrl;

        PortBox.Value = int.TryParse(
            AppSettings.Get(AppSettings.NetworkTestPortKey),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int port) && port is >= 1 and <= 65535
            ? port
            : DefaultPort;
    }

    private void SaveTargets()
    {
        AppSettings.Set(AppSettings.NetworkTestUrlKey, TestUrlBox.Text.Trim());
        AppSettings.Set(AppSettings.NetworkTestHostKey, HostBox.Text.Trim());
        AppSettings.Set(AppSettings.NetworkTestExitIpUrlKey, ExitIpBox.Text.Trim());
        AppSettings.Set(
            AppSettings.NetworkTestPortKey,
            ((int)PortBox.Value).ToString(CultureInfo.InvariantCulture));
    }

    private async void RunBasicButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTestsAsync(proxyMode: false);
    }

    private async void RunProxyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunTestsAsync(proxyMode: true);
    }

    private async Task RunTestsAsync(bool proxyMode)
    {
        if (_isTesting) return;

        if (!TryReadTargets(out Uri? httpUri, out Uri? exitIpUri, out string host, out int port, out string error))
        {
            SummaryTextBlock.Text = error;
            return;
        }

        SaveTargets();
        Results.Clear();
        SetTestingState(true);
        SummaryTextBlock.Text = proxyMode ? "正在测试代理 TUN 链路..." : "正在执行基础网络诊断...";
        LastRunTextBlock.Text = string.Empty;
        _testCts = new CancellationTokenSource();

        try
        {
            var results = proxyMode
                ? await _testService.RunProxyDiagnosticsAsync(
                    httpUri!, exitIpUri!, _testCts.Token)
                : await _testService.RunBasicDiagnosticsAsync(
                    httpUri!, host, port, _testCts.Token);

            foreach (var result in results)
                Results.Add(result);

            int successCount = results.Count(item => item.Status == NetworkTestStatus.Success);
            int failedCount = results.Count(item => item.Status == NetworkTestStatus.Failed);
            int warningCount = results.Count(item => item.Status == NetworkTestStatus.Warning);
            SummaryTextBlock.Text = $"完成：{successCount} 项通过，{warningCount} 项警告，{failedCount} 项失败";
            LastRunTextBlock.Text = $"最近测试：{DateTime.Now:HH:mm:ss}";
            AddHistory(proxyMode, results);
        }
        catch (OperationCanceledException) when (_testCts.IsCancellationRequested)
        {
            SummaryTextBlock.Text = "测试已取消";
        }
        catch (Exception ex)
        {
            SummaryTextBlock.Text = $"测试失败：{ex.Message}";
        }
        finally
        {
            _testCts?.Dispose();
            _testCts = null;
            SetTestingState(false);
        }
    }

    private void LoadHistory()
    {
        try
        {
            var history = JsonSerializer.Deserialize<List<NetworkTestRunSummary>>(
                AppSettings.Get(AppSettings.NetworkTestHistoryKey) ?? "[]") ?? new List<NetworkTestRunSummary>();
            foreach (var item in history.Take(10)) History.Add(item);
        }
        catch { }
    }

    private void AddHistory(bool proxyMode, IReadOnlyCollection<NetworkTestResult> results)
    {
        var summary = new NetworkTestRunSummary
        {
            Timestamp = DateTime.Now,
            Mode = proxyMode ? "代理链路" : "基础诊断",
            Passed = results.Count(item => item.Status == NetworkTestStatus.Success),
            Warnings = results.Count(item => item.Status == NetworkTestStatus.Warning),
            Failed = results.Count(item => item.Status == NetworkTestStatus.Failed),
            FailureText = string.Join("；", results.Where(item => item.Status == NetworkTestStatus.Failed)
                .Select(item => $"{item.Name}: {item.Detail}").Take(3))
        };
        History.Insert(0, summary);
        while (History.Count > 10) History.RemoveAt(History.Count - 1);
        AppSettings.Set(AppSettings.NetworkTestHistoryKey, JsonSerializer.Serialize(History));
    }

    private bool TryReadTargets(
        out Uri? httpUri,
        out Uri? exitIpUri,
        out string host,
        out int port,
        out string error)
    {
        httpUri = null;
        exitIpUri = null;
        host = HostBox.Text.Trim();
        port = 0;
        error = string.Empty;

        if (!Uri.TryCreate(TestUrlBox.Text.Trim(), UriKind.Absolute, out httpUri)
            || (httpUri.Scheme != Uri.UriSchemeHttp && httpUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "HTTP/HTTPS 地址无效，请输入完整 URL。";
            return false;
        }

        if (!Uri.TryCreate(ExitIpBox.Text.Trim(), UriKind.Absolute, out exitIpUri)
            || (exitIpUri.Scheme != Uri.UriSchemeHttp && exitIpUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "出口 IP 服务地址无效，请输入完整 URL。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            error = "DNS/TCP 主机名无效。";
            return false;
        }

        if (double.IsNaN(PortBox.Value)
            || PortBox.Value % 1 != 0
            || PortBox.Value is < 1 or > 65535)
        {
            error = "端口必须是 1 到 65535 之间的整数。";
            return false;
        }

        port = (int)PortBox.Value;
        return true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isTesting) return;
        SummaryTextBlock.Text = "正在取消测试...";
        _testCts?.Cancel();
    }

    private void ClearResultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting) return;
        Results.Clear();
        SummaryTextBlock.Text = "等待测试";
        LastRunTextBlock.Text = string.Empty;
    }

    private void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (Results.Count == 0) return;

        string report = string.Join(
            Environment.NewLine,
            new[]
            {
                "singC 网络测试报告",
                $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"HTTP/HTTPS：{TestUrlBox.Text.Trim()}",
                $"DNS/TCP：{HostBox.Text.Trim()}:{GetPortText()}",
                $"出口 IP 服务：{ExitIpBox.Text.Trim()}",
                string.Empty
            }.Concat(Results.Select(item => item.ReportLine)));

        var package = new DataPackage();
        package.SetText(report);
        Clipboard.SetContent(package);
    }

    private string GetPortText()
    {
        return double.IsNaN(PortBox.Value)
            ? "--"
            : PortBox.Value.ToString(CultureInfo.InvariantCulture);
    }

    private void SetTestingState(bool testing)
    {
        _isTesting = testing;
        RunBasicButton.IsEnabled = !testing;
        RunProxyButton.IsEnabled = !testing;
        CancelButton.IsEnabled = testing;
        TestUrlBox.IsEnabled = !testing;
        HostBox.IsEnabled = !testing;
        PortBox.IsEnabled = !testing;
        ExitIpBox.IsEnabled = !testing;
    }
}
