using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace singC.Models;

public sealed class TrafficStatisticsViewModel : INotifyPropertyChanged
{
    private readonly TrafficStatistics _statistics = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "singC", "traffic-statistics.json"));
    private readonly DispatcherQueueTimer _timer;
    private TrafficStatisticsSnapshot _snapshot;
    private WebSocketConnectionState _state;

    public TrafficStatisticsViewModel(DispatcherQueue dispatcher)
    {
        _snapshot = _statistics.GetSnapshot(DateTimeOffset.Now, TrafficStatistics.MonotonicSeconds);
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public TrafficTotals Session => _snapshot.Session;
    public TrafficTotals Today => _snapshot.Today;
    public TrafficTotals Month => _snapshot.Month;
    public TrafficTotals AllTime => _snapshot.AllTime;
    public IReadOnlyList<DailyTraffic> RecentDays => _snapshot.RecentDays;
    public string UploadSpeed => TrafficTotals.FormatBytes(_snapshot.UploadSpeed) + "/s";
    public string DownloadSpeed => TrafficTotals.FormatBytes(_snapshot.DownloadSpeed) + "/s";
    public bool HasLiveData => _state == WebSocketConnectionState.Connected
        && _snapshot.LastSample is { } last && DateTimeOffset.Now - last <= TimeSpan.FromSeconds(5);
    public string LastUpdated => _snapshot.LastSample is { } time ? $"最近采样：{time:yyyy-MM-dd HH:mm:ss}" : "尚未收到流量数据";
    public string StorageError => _snapshot.StorageError;
    public string Status => _state switch
    {
        WebSocketConnectionState.Connected => _snapshot.LastSample is not { } last
            || DateTimeOffset.Now - last > TimeSpan.FromSeconds(5) ? "等待流量数据" : "正在统计",
        WebSocketConnectionState.Connecting => "正在连接 sing-box",
        WebSocketConnectionState.Reconnecting => "连接中断，正在重连",
        WebSocketConnectionState.Failed => "无法连接，请检查 sing-box 的 Clash API 配置",
        _ => "已停止采集，历史数据已保留"
    };

    public void BeginSession() { _statistics.BeginSession(); Refresh(); }
    public void Record(long upload, long download)
    {
        _statistics.Record(upload, download, DateTimeOffset.Now, TrafficStatistics.MonotonicSeconds);
        _statistics.Save();
    }
    public void SetConnectionState(WebSocketConnectionState state)
    {
        _state = state;
        if (state != WebSocketConnectionState.Connected) _statistics.MarkDisconnected();
        Refresh();
    }
    public void Save() => _statistics.Save(force: true);
    private void Refresh()
    {
        _snapshot = _statistics.GetSnapshot(DateTimeOffset.Now, TrafficStatistics.MonotonicSeconds);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
