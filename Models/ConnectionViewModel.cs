using Microsoft.UI.Dispatching;
using singC.Helpers;
using singC.Models;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

public class ConnectionViewModel : INotifyPropertyChanged
{
    private static ConnectionViewModel? _instance;
    private static readonly object _lock = new();
    public static ConnectionViewModel Instance
    {
        get
        {
            if (_instance == null)
                throw new InvalidOperationException("请先调用 Initialize(dispatcher)");
            return _instance;
        }
    }

    public ObservableCollection<ConnectionInfo> Connections { get; } = new();
    public ObservableCollection<ConnectionInfo> FilteredConnections { get; } = new();
    public TrafficStatisticsViewModel Traffic { get; }

    private readonly DispatcherQueue _dispatcher;
    private ClashWebSocketService? _wsService;
    private DispatcherQueueTimer? _refreshTimer;
    private List<ConnectionInfo> _latestConnections = new();
    private readonly object _latestConnectionsLock = new();
    private bool _isStarting;
    private readonly SemaphoreSlim _serviceGate = new(1, 1);
    private long _runGeneration;
    private readonly object _sampleGate = new();
    private long _subscriptionVersion;
    private bool _shuttingDown;
    private const int DefaultRefreshIntervalSeconds = 1;
    private const int MaxReconnectAttempts = 10;

    private bool _autoReconnect;
    private int _refreshIntervalSeconds;
    private string _sortMode = "time";
    private string _connectionStatus = "未连接";
    private string _lastError = string.Empty;
    private int _reconnectAttempt;

    public bool AutoReconnect
    {
        get => _autoReconnect;
        set
        {
            if (_autoReconnect == value) return;
            if (!TrySavePreference(AppSettings.AutoReconnectKey, value.ToString(), nameof(AutoReconnect))) return;
            _autoReconnect = value;
            if (_wsService != null) _wsService.AutoReconnect = value;
            OnPropertyChanged();
        }
    }

    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            int normalized = Math.Clamp(value, 1, 10);
            if (_refreshIntervalSeconds == normalized) return;
            if (!TrySavePreference(AppSettings.ConnectionRefreshIntervalKey, normalized.ToString(), nameof(RefreshIntervalSeconds))) return;
            _refreshIntervalSeconds = normalized;
            if (_refreshTimer != null) _refreshTimer.Interval = TimeSpan.FromSeconds(normalized);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RefreshIntervalIndex));
        }
    }

    public int RefreshIntervalIndex
    {
        get => RefreshIntervalSeconds switch { 1 => 0, 2 => 1, 5 => 2, _ => 3 };
        set => RefreshIntervalSeconds = value switch { 0 => 1, 1 => 2, 2 => 5, _ => 10 };
    }

    public string SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode == value) return;
            if (!TrySavePreference(AppSettings.ConnectionSortKey, value, nameof(SortMode))) return;
            _sortMode = value;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set { if (_connectionStatus != value) { _connectionStatus = value; OnPropertyChanged(); } }
    }

    public string LastError
    {
        get => _lastError;
        private set { if (_lastError != value) { _lastError = value; OnPropertyChanged(); } }
    }

    public int ReconnectAttempt
    {
        get => _reconnectAttempt;
        private set { if (_reconnectAttempt != value) { _reconnectAttempt = value; OnPropertyChanged(); } }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                ApplyFilter();
            }
        }
    }

    public void RefreshNow()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(RefreshNow);
            return;
        }

        List<ConnectionInfo> snapshot;
        lock (_latestConnectionsLock)
            snapshot = _latestConnections.ToList();

        SyncConnections(snapshot);
    }

    private ConnectionViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Traffic = new TrafficStatisticsViewModel(dispatcher);
        _autoReconnect = !bool.TryParse(AppSettings.Get(AppSettings.AutoReconnectKey), out bool autoReconnect) || autoReconnect;
        _refreshIntervalSeconds = int.TryParse(AppSettings.Get(AppSettings.ConnectionRefreshIntervalKey), out int interval)
            ? Math.Clamp(interval, 1, 10)
            : DefaultRefreshIntervalSeconds;
        _sortMode = AppSettings.Get(AppSettings.ConnectionSortKey) ?? "time";
        _refreshTimer = _dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(_refreshIntervalSeconds);
        _refreshTimer.Tick += (s, e) =>
        {
            List<ConnectionInfo> snapshot;
            lock (_latestConnectionsLock)
                snapshot = _latestConnections.ToList();

            SyncConnections(snapshot);
        };
    }

    public static void Initialize(DispatcherQueue dispatcher)
    {
        lock (_lock)
        {
            if (_instance != null) return;
            _instance = new ConnectionViewModel(dispatcher);
            _instance.SubscribeToSingBox();
        }
    }

    private void SubscribeToSingBox()
    {
        SingBoxService.Instance.StateChanged += () =>
        {
            _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await SynchronizeServiceAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"连接状态同步失败: {ex.Message}");
                }
            });
        };

        if (SingBoxService.Instance.IsRunning)
            _dispatcher.TryEnqueue(async () => await SynchronizeServiceAsync());
    }

    private async Task SynchronizeServiceAsync()
    {
        await _serviceGate.WaitAsync();
        try
        {
            if (_shuttingDown) return;
            if (SingBoxService.Instance.IsRunning)
            {
                if (_wsService != null && _runGeneration != SingBoxService.Instance.RunGeneration)
                    await StopAsync();
                await StartAsync();
            }
            else await StopAsync();
        }
        finally { _serviceGate.Release(); }
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        await _serviceGate.WaitAsync();
        try { await StopAsync(); Traffic.Dispose(); }
        catch { _shuttingDown = false; throw; }
        finally { _serviceGate.Release(); }
    }

    private void OnConnectionsReceived(List<ConnectionInfo> newList)
    {
        lock (_latestConnectionsLock)
            _latestConnections = newList;
    }

    private async Task StartAsync()
    {
        if (_wsService != null || _isStarting) return;

        _isStarting = true;
        ClashWebSocketService? service = null;
        try
        {
            await Task.Delay(1000);
            if (_shuttingDown || !SingBoxService.Instance.IsRunning) return;
            var endpoint = SingBoxService.Instance.GetActiveClashApiEndpoint();
            _runGeneration = SingBoxService.Instance.RunGeneration;
            long generation = _runGeneration;
            long version;
            lock (_sampleGate) { version = ++_subscriptionVersion; Traffic.BeginSession(); }
            service = new ClashWebSocketService(endpoint.Address.ToString(), endpoint.Secret);
            service.AutoReconnect = AutoReconnect;
            service.OnConnectionsReceived += list =>
            {
                lock (_sampleGate) if (version == _subscriptionVersion && generation == SingBoxService.Instance.RunGeneration && SingBoxService.Instance.IsRunning) OnConnectionsReceived(list);
            };
            service.OnTrafficTotalsReceived += (up, down) =>
            {
                lock (_sampleGate) if (version == _subscriptionVersion && generation == SingBoxService.Instance.RunGeneration && SingBoxService.Instance.IsRunning) Traffic.Record(up, down);
            };
            service.OnConnectionTrafficReceived += samples =>
            {
                lock (_sampleGate) if (version == _subscriptionVersion && generation == SingBoxService.Instance.RunGeneration && SingBoxService.Instance.IsRunning) Traffic.RecordConnections(samples);
            };
            service.ConnectionStateChanged += (state, attempt, error) => OnWebSocketStateChanged(version, generation, state, attempt, error);
            await service.StartWebSocketAsync();
            _wsService = service;
            ConnectionStatus = service.ConnectionState == WebSocketConnectionState.Connected ? "已连接" : "连接中";
            _dispatcher.TryEnqueue(() => _refreshTimer?.Start());
        }
        catch (Exception ex)
        {
            if (service != null)
            {
                service.Dispose();
            }

            System.Diagnostics.Debug.WriteLine($"连接 WebSocket 启动失败: {ex.Message}");
            Traffic.SetConnectionState(WebSocketConnectionState.Failed);
            ConnectionStatus = "采集失败";
            LastError = ex.Message;
            _dispatcher.TryEnqueue(() =>
            {
                lock (_latestConnectionsLock)
                    _latestConnections.Clear();

                SyncConnections(new List<ConnectionInfo>());
            });
        }
        finally
        {
            _isStarting = false;
        }
    }

    private async Task StopAsync()
    {
        lock (_sampleGate) ++_subscriptionVersion;
        lock (_latestConnectionsLock)
            _latestConnections.Clear();

        SyncConnections(new List<ConnectionInfo>());
        _refreshTimer?.Stop();
        if (_wsService != null)
        {
            await _wsService.StopAsync();
            _wsService.Dispose();
            _wsService = null;
        }
        Traffic.SetConnectionState(WebSocketConnectionState.Stopped);
        await Task.Run(Traffic.Save);
        ConnectionStatus = "未连接";
        LastError = string.Empty;
        ReconnectAttempt = 0;
    }

    private void OnWebSocketStateChanged(long version, long generation, WebSocketConnectionState state, int attempt, string error)
    {
        _dispatcher.TryEnqueue(() =>
        {
            lock (_sampleGate) if (version != _subscriptionVersion || generation != SingBoxService.Instance.RunGeneration) return;
            Traffic.SetConnectionState(state);
            ReconnectAttempt = attempt;
            LastError = error;
            ConnectionStatus = state switch
            {
                WebSocketConnectionState.Connected => "已连接",
                WebSocketConnectionState.Connecting => "连接中",
                WebSocketConnectionState.Reconnecting => $"重连中（第 {attempt}/{MaxReconnectAttempts} 次）",
                WebSocketConnectionState.Failed => "连接失败",
                _ => "未连接"
            };
        });
    }

    private void SyncConnections(List<ConnectionInfo> newList)
    {
        var newMap = newList.GroupBy(c => c.Id).ToDictionary(group => group.Key, group => group.First());
        var existingMap = Connections.GroupBy(c => c.Id).ToDictionary(group => group.Key, group => group.First());
        bool changed = false;

        // 移除不再存在的连接
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            if (!newMap.ContainsKey(Connections[i].Id))
            {
                Connections.RemoveAt(i);
                changed = true;
            }
        }

        // 更新现有连接或添加新连接
        foreach (var kvp in newMap)
        {
            existingMap.TryGetValue(kvp.Key, out var existing);
            if (existing != null)
            {
                if (existing.UpdateFrom(kvp.Value))   // 返回值表示是否实际变化
                    changed = true;
            }
            else
            {
                Connections.Add(kvp.Value);
                changed = true;
            }
        }

        // 仅当实际变化时才重新过滤
        if (changed)
            ApplyFilter();
    }

    private void ApplyFilter()
    {
        var desired = ConnectionPresentation.Select(Connections, SearchText, SortMode).ToList();

        // 比较当前 FilteredConnections 与 desired，如果完全一致则跳过
        if (FilteredConnections.SequenceEqual(desired))
            return;

        // 否则重建过滤集合
        FilteredConnections.Clear();
        foreach (var conn in desired)
            FilteredConnections.Add(conn);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool TrySavePreference(string key, string value, string property)
    {
        try { AppSettings.Set(key, value); return true; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            LastError = "设置保存失败：" + ex.Message;
            OnPropertyChanged(property);
            return false;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
