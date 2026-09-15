using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using singC.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace singC.Pages;

public sealed partial class LogPage : Page
{
    private readonly DispatcherQueue _uiQueue = DispatcherQueue.GetForCurrentThread();
    private readonly SingBoxService _singBox = SingBoxService.Instance;
    private const int MaxLogCount = 1000;
    private readonly object _pendingLogLock = new();
    private readonly List<string> _pendingLogs = new();
    private readonly DispatcherQueueTimer _flushTimer;
    private readonly DispatcherQueueTimer _analysisTimer;
    private ScrollViewer? _logScrollViewer;
    private bool _isFollowingLatest = true;
    private bool _filterDirty;
    private bool _isProgrammaticScroll;
    private string _lastAnalyzerSnapshot = string.Empty;

    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<LogEntry> FilteredLogs { get; } = new();
    public ObservableCollection<string> AnalyzerKeywords { get; } = new();

    public LogPage()
    {
        InitializeComponent();
        _flushTimer = _uiQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(500);
        _flushTimer.Tick += FlushTimer_Tick;
        _flushTimer.Start();

        _analysisTimer = _uiQueue.CreateTimer();
        _analysisTimer.Interval = TimeSpan.FromSeconds(1);
        _analysisTimer.Tick += AnalysisTimer_Tick;
        _analysisTimer.Start();

        _singBox.ErrorDataReceived += OnErrorDataReceived;
        RebuildFilteredLogs();
        UpdateFollowButton();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        _singBox.ErrorDataReceived -= OnErrorDataReceived;
        _flushTimer.Stop();
        _analysisTimer.Stop();
        base.OnNavigatedFrom(e);
    }

    public static string CleanAnsiSequences(string input)
    {
        return Regex.Replace(input, @"\x1b\[[\d;]*[\x40-\x7E]", string.Empty);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        string cleanLog = CleanAnsiSequences(e.Data);
        lock (_pendingLogLock)
        {
            _pendingLogs.Add(cleanLog);
        }
    }

    private void FlushTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        List<string> batch;
        lock (_pendingLogLock)
        {
            if (_pendingLogs.Count == 0 && !_filterDirty) return;

            batch = _pendingLogs.ToList();
            _pendingLogs.Clear();
        }

        bool visibleLogAdded = false;
        foreach (var message in batch)
            visibleLogAdded |= AppendLogEntry(LogEntry.Create(message));

        if (_filterDirty)
            RebuildFilteredLogs();

        if (_isFollowingLatest && visibleLogAdded)
            ScrollToLatest();
    }

    private void AnalysisTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        UpdateAnalyzer(FilteredLogs.ToList());
    }

    private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterDirty = true;
        RebuildFilteredLogs();
        if (_isFollowingLatest)
            ScrollToLatest();
    }

    private void LevelFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _filterDirty = true;
        RebuildFilteredLogs();
        if (_isFollowingLatest)
            ScrollToLatest();
    }

    private void CopySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (LogListView.SelectedItem is not LogEntry entry) return;

        CopyToClipboard(entry.CopyText);
    }

    private void CopyFilteredButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilteredLogs.Count == 0) return;

        string text = string.Join(Environment.NewLine, FilteredLogs.Select(item => item.CopyText));
        CopyToClipboard(text);
    }

    private async void ExportFilteredButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilteredLogs.Count == 0) return;

        var picker = new FileSavePicker
        {
            SuggestedFileName = $"singC-log-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.window));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        string text = string.Join(Environment.NewLine, FilteredLogs.Select(item => item.CopyText));
        await Windows.Storage.FileIO.WriteTextAsync(file, text);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        lock (_pendingLogLock)
        {
            _pendingLogs.Clear();
        }

        Logs.Clear();
        FilteredLogs.Clear();
        AnalyzerKeywords.Clear();
        LogListView.SelectedItem = null;
        _lastAnalyzerSnapshot = string.Empty;
        UpdateAnalyzer(FilteredLogs.ToList());
    }

    private void FollowButton_Click(object sender, RoutedEventArgs e)
    {
        _isFollowingLatest = !_isFollowingLatest;
        UpdateFollowButton();
        if (_isFollowingLatest)
            ScrollToLatest();
    }

    private void JumpToLatestButton_Click(object sender, RoutedEventArgs e)
    {
        _isFollowingLatest = true;
        UpdateFollowButton();
        ScrollToLatest();
    }

    private void LogListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogListView.SelectedItem != null)
            SetFollowingLatest(false);
    }

    private void LogListView_Loaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer = FindDescendant<ScrollViewer>(LogListView);
        if (_logScrollViewer != null)
            _logScrollViewer.ViewChanged += LogScrollViewer_ViewChanged;
    }

    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isProgrammaticScroll || e.IsIntermediate || _logScrollViewer == null) return;

        bool nearBottom = _logScrollViewer.ScrollableHeight - _logScrollViewer.VerticalOffset <= 4;
        if (!nearBottom)
            SetFollowingLatest(false);
    }

    private bool AppendLogEntry(LogEntry entry)
    {
        Logs.Add(entry);
        while (Logs.Count > MaxLogCount)
        {
            var removed = Logs[0];
            Logs.RemoveAt(0);
            FilteredLogs.Remove(removed);
        }

        if (_filterDirty) return false;
        if (MatchesCurrentFilter(entry))
        {
            FilteredLogs.Add(entry);
            return true;
        }

        return false;
    }

    private void RebuildFilteredLogs()
    {
        if (FilteredLogs == null) return;

        var filtered = Logs.Where(MatchesCurrentFilter).ToList();

        FilteredLogs.Clear();
        foreach (var item in filtered)
            FilteredLogs.Add(item);

        _filterDirty = false;
        UpdateAnalyzer(filtered);
    }

    private bool MatchesCurrentFilter(LogEntry log)
    {
        string keyword = FilterTextBox?.Text?.Trim() ?? string.Empty;
        string level = GetSelectedLevel();

        return (level == "All" || log.Level == level) &&
            (string.IsNullOrEmpty(keyword) || log.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private string GetSelectedLevel()
    {
        if (LevelFilterComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag;

        return "All";
    }

    private void SetFollowingLatest(bool value)
    {
        if (_isFollowingLatest == value) return;

        _isFollowingLatest = value;
        UpdateFollowButton();
    }

    private void UpdateFollowButton()
    {
        if (FollowButton != null)
            FollowButton.Content = _isFollowingLatest ? "暂停跟随" : "继续跟随";
    }

    private void ScrollToLatest()
    {
        var latest = FilteredLogs.LastOrDefault();
        if (latest == null) return;

        _isProgrammaticScroll = true;
        if (_logScrollViewer != null)
            _logScrollViewer.ChangeView(null, _logScrollViewer.ScrollableHeight, null, true);
        else
            LogListView.ScrollIntoView(latest);
        _uiQueue.TryEnqueue(() => _isProgrammaticScroll = false);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            var nested = FindDescendant<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void UpdateAnalyzer(IReadOnlyCollection<LogEntry> logs)
    {
        if (TotalCountTextBlock == null) return;

        int errorCount = logs.Count(item => item.Level == LogLevels.Error);
        int warningCount = logs.Count(item => item.Level == LogLevels.Warning);
        int infoCount = logs.Count - errorCount - warningCount;
        string latestIssue = logs.LastOrDefault(item =>
            item.Level == LogLevels.Error || item.Level == LogLevels.Warning)?.Message ?? "暂无错误或警告";
        var keywords = BuildKeywordSummary(logs);
        string snapshot = $"{logs.Count}|{errorCount}|{warningCount}|{infoCount}|{latestIssue}|{string.Join(";", keywords)}";

        if (snapshot == _lastAnalyzerSnapshot) return;
        _lastAnalyzerSnapshot = snapshot;

        TotalCountTextBlock.Text = logs.Count.ToString();
        ErrorCountTextBlock.Text = errorCount.ToString();
        WarningCountTextBlock.Text = warningCount.ToString();
        InfoCountTextBlock.Text = infoCount.ToString();

        LatestIssueTextBlock.Text = latestIssue;

        AnalyzerKeywords.Clear();
        foreach (var keyword in keywords)
            AnalyzerKeywords.Add(keyword);
    }

    private static IReadOnlyList<string> BuildKeywordSummary(IEnumerable<LogEntry> logs)
    {
        var logList = logs.ToList();
        var keywords = new[] { "error", "failed", "timeout", "dns", "connect", "reject", "warn", "panic" };
        return keywords
            .Select(keyword => new
            {
                Keyword = keyword,
                Count = logList.Count(log => log.Message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            })
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .Take(6)
            .Select(item => $"{item.Keyword}: {item.Count}")
            .DefaultIfEmpty("暂无高频关键词")
            .ToList();
    }
}

public static class LogLevels
{
    public const string Error = "Error";
    public const string Warning = "Warning";
    public const string Info = "Info";
    public const string Debug = "Debug";
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; }
    public string TimeText => Timestamp.ToString("HH:mm:ss");
    public string Level { get; }
    public string Message { get; }
    public string CopyText => $"{Timestamp:yyyy-MM-dd HH:mm:ss}\t{Level}\t{Message}";
    public SolidColorBrush Foreground { get; }
    public SolidColorBrush Background { get; }

    private LogEntry(DateTime timestamp, string level, string message, SolidColorBrush foreground, SolidColorBrush background)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
        Foreground = foreground;
        Background = background;
    }

    public static LogEntry Create(string message)
    {
        string level = DetectLevel(message);
        return level switch
        {
            LogLevels.Error => new LogEntry(
                DateTime.Now,
                level,
                message,
                new SolidColorBrush(Colors.Firebrick),
                new SolidColorBrush(Windows.UI.Color.FromArgb(28, 196, 43, 28))),
            LogLevels.Warning => new LogEntry(
                DateTime.Now,
                level,
                message,
                new SolidColorBrush(Colors.DarkGoldenrod),
                new SolidColorBrush(Windows.UI.Color.FromArgb(28, 157, 93, 0))),
            LogLevels.Debug => new LogEntry(
                DateTime.Now,
                level,
                message,
                new SolidColorBrush(Colors.DimGray),
                new SolidColorBrush(Windows.UI.Color.FromArgb(10, 96, 96, 96))),
            _ => new LogEntry(
                DateTime.Now,
                LogLevels.Info,
                message,
                new SolidColorBrush(Colors.DarkSlateGray),
                new SolidColorBrush(Windows.UI.Color.FromArgb(12, 0, 120, 212)))
        };
    }

    private static string DetectLevel(string message)
    {
        if (Regex.IsMatch(message, @"\b(error|failed|fatal|panic|exception)\b", RegexOptions.IgnoreCase))
            return LogLevels.Error;

        if (Regex.IsMatch(message, @"\b(warn|warning|timeout|reject)\b", RegexOptions.IgnoreCase))
            return LogLevels.Warning;

        if (Regex.IsMatch(message, @"\b(debug|trace)\b", RegexOptions.IgnoreCase))
            return LogLevels.Debug;

        return LogLevels.Info;
    }
}
