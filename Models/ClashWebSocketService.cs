using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models
{
    public enum WebSocketConnectionState
    {
        Stopped,
        Connecting,
        Connected,
        Reconnecting,
        Failed
    }

    public class ClashWebSocketService : IDisposable
    {
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private TaskCompletionSource<bool>? _firstConnection;
        private readonly string _baseUrl;          // 如 "http://127.0.0.1:9090"
        private readonly string? _secret;
        private static HttpClient _httpClient = new HttpClient();
        internal static HttpClient SharedHttpClient => _httpClient;
        public WebSocketConnectionState ConnectionState { get; private set; } = WebSocketConnectionState.Stopped;
        public int ReconnectAttempt { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public bool AutoReconnect { get; set; } = true;

        // 事件：当 WebSocket 推送连接数据时触发
        public event Action<List<ConnectionInfo>>? OnConnectionsReceived;
        public event Action<WebSocketConnectionState, int, string>? ConnectionStateChanged;

        public ClashWebSocketService(string baseUrl = "http://127.0.0.1:9090", string? secret = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _secret = secret;
        }

        public async Task StartWebSocketAsync(CancellationToken cancellationToken = default)
        {
            if (_runTask is { IsCompleted: false }) return;

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _firstConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runTask = RunConnectionLoopAsync(_cts.Token);

            try
            {
                await _firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (TimeoutException)
            {
                // 后台循环会继续按退避策略重试。
            }
        }

        private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
        {
            string wsUrl = _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/connections";
            ReconnectAttempt = 0;
            bool firstAttempt = true;

            while (!cancellationToken.IsCancellationRequested
                && (firstAttempt || (AutoReconnect && ReconnectAttempt <= 10)))
            {
                firstAttempt = false;
                ClientWebSocket? socket = null;
                try
                {
                    SetConnectionState(ReconnectAttempt == 0
                        ? WebSocketConnectionState.Connecting
                        : WebSocketConnectionState.Reconnecting);
                    socket = new ClientWebSocket();
                    if (!string.IsNullOrEmpty(_secret))
                        socket.Options.SetRequestHeader("Authorization", $"Bearer {_secret}");

                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await socket.ConnectAsync(new Uri(wsUrl), connectTimeout.Token);
                    _webSocket = socket;
                    ReconnectAttempt = 0;
                    SetConnectionState(WebSocketConnectionState.Connected);
                    _firstConnection?.TrySetResult(true);
                    await ReceiveLoopAsync(socket, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    _firstConnection?.TrySetResult(false);
                    ReconnectAttempt++;
                    SetConnectionState(ReconnectAttempt > 10
                        ? WebSocketConnectionState.Failed
                        : WebSocketConnectionState.Reconnecting);
                }
                finally
                {
                    if (ReferenceEquals(_webSocket, socket)) _webSocket = null;
                    socket?.Dispose();
                }

                if (AutoReconnect && !cancellationToken.IsCancellationRequested && ReconnectAttempt <= 10)
                {
                    if (ReconnectAttempt == 0) ReconnectAttempt = 1;
                    int delaySeconds = Math.Min(30, 1 << Math.Min(ReconnectAttempt - 1, 4));
                    try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }

            if (cancellationToken.IsCancellationRequested)
                SetConnectionState(WebSocketConnectionState.Stopped);
            else
                SetConnectionState(WebSocketConnectionState.Failed);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(message.ToString());
                if (doc.RootElement.TryGetProperty("connections", out var arr))
                    OnConnectionsReceived?.Invoke(ParseConnections(arr));
            }
        }

        private List<ConnectionInfo> ParseConnections(JsonElement connArray)
        {
            var list = new List<ConnectionInfo>();
            foreach (var item in connArray.EnumerateArray())
            {
                var metadata = item.GetProperty("metadata");
                var info = new ConnectionInfo
                {
                    Id = item.GetProperty("id").GetString() ?? "",
                    Network = metadata.GetProperty("network").GetString() ?? "",
                    Source = $"{metadata.GetProperty("sourceIP").GetString()}:{metadata.GetProperty("sourcePort").GetString()}",
                    Destination = $"{metadata.GetProperty("destinationIP").GetString()}:{metadata.GetProperty("destinationPort").GetString()}",
                    Host = metadata.TryGetProperty("host", out var hostElem) ? hostElem.GetString() ?? "" : "",
                    StartTime = DateTime.Parse(item.GetProperty("start").GetString()!),
                    UploadBytes = item.GetProperty("upload").GetInt64(),
                    DownloadBytes = item.GetProperty("download").GetInt64(),
                    Rule = item.GetProperty("rule").GetString()?? "",
                };

                // 也可尝试解析 Rule 等额外字段
                list.Add(info);
            }
            return list;
        }

        // 停止 WebSocket，释放资源
        public async Task StopAsync()
        {
            _cts?.Cancel();
            var socket = _webSocket;
            if (socket?.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch { }
            }

            if (_runTask != null)
            {
                try { await _runTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { }
            }
            _runTask = null;
            _webSocket = null;
            SetConnectionState(WebSocketConnectionState.Stopped);
        }

        private void SetConnectionState(WebSocketConnectionState state)
        {
            ConnectionState = state;
            try { ConnectionStateChanged?.Invoke(state, ReconnectAttempt, LastError); }
            catch { }
        }
        public static async Task<TrafficData?> FetchTrafficDataAsync()
        {
            var trafficDataUrl = singC.Helpers.AppSettings.Get(singC.Helpers.AppSettings.TrafficServiceUrlKey);
            if (!singC.Helpers.AppSettings.IsServiceUrl(trafficDataUrl))
                return null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using HttpResponseMessage response = await _httpClient.GetAsync(trafficDataUrl, timeout.Token);
                if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
                    response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync(timeout.Token);
                TrafficData? data = JsonSerializer.Deserialize<TrafficData>(json);
                if (data != null && !response.IsSuccessStatusCode)
                    data.Error ??= "流量服务暂时不可用";
                return data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ERR] 获取流量数据失败: {ex.Message}");
                return null;
            }
        }


        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _webSocket?.Dispose();
        }
    }

    // 保留原有的数据模型类，无需改动
    
}
