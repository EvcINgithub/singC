using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using singC.Models;

internal static class ConnectionStorageTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string Config(string address, object? secret = null) => JsonSerializer.Serialize(new { experimental = new { clash_api = new { external_controller = address, secret } } });
        foreach (var pair in new[] { ("127.0.0.1:9191", "http://127.0.0.1:9191/"), ("0.0.0.0:9191", "http://127.0.0.1:9191/"), ("[::]:9191", "http://[::1]:9191/"), ("https://localhost:9191", "https://localhost:9191/") })
        {
            var endpoint = ClashApiEndpoint.FromConfig(Config(pair.Item1, "private-token"));
            check(endpoint.Address.AbsoluteUri == pair.Item2 && endpoint.Secret == "private-token"
                && !endpoint.ToString().Contains("private-token"), "collector: running endpoint normalization " + pair.Item1);
        }
        foreach (string bad in new[] { "", "ftp://localhost", "http://user:pass@localhost", "http://localhost/path", "http://localhost?x", "http://localhost#x", "http://[broken" })
        {
            try { ClashApiEndpoint.FromConfig(Config(bad)); throw new Exception("Expected invalid endpoint"); }
            catch (InvalidDataException) { check(true, "collector: invalid endpoint rejected " + bad); }
        }
        foreach (string missing in new[] { "{}", "{\"experimental\":{}}", "{\"experimental\":{\"clash_api\":{}}}" })
        {
            try { ClashApiEndpoint.FromConfig(missing); throw new Exception("Expected missing endpoint"); }
            catch (InvalidDataException) { check(true, "collector: absent endpoint is explicit"); }
        }
        check(ClashApiEndpoint.FromConfig(Config("localhost:9090")).Secret == null, "collector: optional secret remains absent");
        using var service = new ClashWebSocketService();
        int totalEvents = 0, connectionEvents = 0;
        IReadOnlyList<ConnectionTrafficSample> samples = [];
        List<ConnectionInfo> connections = [];
        service.OnTrafficTotalsReceived += (_, _) => totalEvents++;
        service.OnConnectionTrafficReceived += value => samples = value;
        service.OnConnectionsReceived += value => { connections = value; connectionEvents++; };
        string valid = """{"id":"a","start":"2026-10-09T00:00:00Z","upload":10,"download":20,"chains":["node","proxy"],"metadata":{"network":"tcp","host":"example.test","sourceIP":"127.0.0.1","sourcePort":123,"destinationIP":"1.2.3.4","destinationPort":"443"},"rule":"test-rule"}""";
        service.ProcessMessage(Encoding.UTF8.GetBytes("{\"uploadTotal\":10,\"downloadTotal\":20,\"connections\":[{}," + valid + "," + valid + ",null]}"));
        check(totalEvents == 1 && connectionEvents == 1 && connections.Count == 1 && samples.Count == 1,
            "collector: malformed rows and duplicate IDs cannot break a frame");
        var c = connections[0];
        check(c.Id == "a" && c.Host == "example.test" && c.Network == "tcp" && c.Source == "127.0.0.1:123"
            && c.Destination == "1.2.3.4:443" && c.Rule == "test-rule" && c.OutboundTag == "proxy"
            && c.UploadBytes == 10 && c.DownloadBytes == 20, "collector: snapshot preserves all connection fields");
        service.ProcessMessage(Encoding.UTF8.GetBytes("{"));
        check(totalEvents == 1 && connectionEvents == 1 && service.LastError.Contains("JSON"), "collector: malformed JSON changes no consumer state");
        foreach (string message in new[] { "{}", "null", "{\"connections\":{}}" }) service.ProcessMessage(Encoding.UTF8.GetBytes(message));
        check(connectionEvents == 1, "collector: missing or invalid list is not an empty snapshot");
        service.ProcessMessage(Encoding.UTF8.GetBytes("{\"connections\":null}"));
        check(connectionEvents == 2 && samples.Count == 0 && connections.Count == 0, "collector: explicit null list means empty snapshot");
        using (var socket = new FakeSocket(Encoding.UTF8.GetBytes("fragmented"), 2))
            check(Encoding.UTF8.GetString((await ClashWebSocketService.ReadMessageAsync(socket, default))!) == "fragmented", "collector: fragmented frame assembled");
        using (var socket = new FakeSocket([], 2, WebSocketMessageType.Close))
            check(await ClashWebSocketService.ReadMessageAsync(socket, default) == null, "collector: close frame terminates receive");
        foreach (int size in new[] { ClashWebSocketService.MaximumMessageBytes, ClashWebSocketService.MaximumMessageBytes + 1 })
        {
            using var socket = new FakeSocket(new byte[size], 8192);
            try
            {
                var frame = await ClashWebSocketService.ReadMessageAsync(socket, default);
                check(size == ClashWebSocketService.MaximumMessageBytes && frame!.Length == size, "collector: frame limit accepted exactly");
            }
            catch (InvalidDataException) { check(size > ClashWebSocketService.MaximumMessageBytes, "collector: oversized frame rejected"); }
        }
        using (var socket = new FakeSocket([], 2, WebSocketMessageType.Binary))
        {
            try { await ClashWebSocketService.ReadMessageAsync(socket, default); throw new Exception("Expected binary rejection"); }
            catch (InvalidDataException) { check(true, "collector: binary frame rejected"); }
        }
        using (var socket = new FakeSocket([], 2))
        {
            try { await ClashWebSocketService.ReadMessageAsync(socket, new CancellationToken(true)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { check(true, "collector: receive observes cancellation"); }
        }
        var time = DateTimeOffset.Now;
        string path = Path.Combine(root, "shared.json");
        using (var writer = new TrafficStatistics(path))
        {
            writer.Record(10, 20, time, 1); writer.Save(true);
            using var reader = new TrafficStatistics(path);
            reader.Record(100, 200, time, 1); reader.Save(true);
            check(reader.GetSnapshot(time, 1).StorageError.Contains("只读"), "statistics: second instance reports read-only ownership");
            using var disk = new TrafficStatistics(path);
            check(disk.GetSnapshot(time, 1).AllTime == new TrafficTotals(10, 20), "statistics: second instance cannot overwrite history");
        }
        using (var reopened = new TrafficStatistics(path))
        {
            check(reopened.GetSnapshot(time, 1).StorageError == "", "statistics: disposal releases writer lease");
            reopened.Record(1, 2, time, 1); reopened.Save(true);
        }
        using (var disk = new TrafficStatistics(path))
            check(disk.GetSnapshot(time, 1).AllTime == new TrafficTotals(11, 22), "statistics: new owner appends restored history");
        double seconds = 0;
        string nestedPath = Path.Combine(root,"new-history-directory","stats.json");
        using (var nested = new StatisticsFileStore<Dictionary<string,int>>(nestedPath))
            check(nested.TrySave(new(){["saved"]=3},true) && File.Exists(nestedPath), "statistics: first save creates parent directory");
        string blockedPath=Path.Combine(root,"blocked-history.json");
        using (var blocked = new StatisticsFileStore<Dictionary<string,int>>(blockedPath))
        {
            Directory.CreateDirectory(blockedPath);
            check(!blocked.TrySave(new(){["value"]=1},true) && blocked.Error.Contains("统计保存失败"), "statistics: replacement failure reports retryable error");
            check(!Directory.EnumerateFiles(root,"*.tmp").Any(), "statistics: replacement failure removes temporary file");
            Directory.Delete(blockedPath);
            check(blocked.TrySave(new(){["value"]=2},true) && blocked.Error == "", "statistics: successful retry clears save error");
        }
        using var file = new StatisticsFileStore<Dictionary<string, int>>(Path.Combine(root, "throttle.json"), () => seconds);
        check(file.Load(_ => true).Count == 0, "statistics: missing file loads empty");
        check(file.TrySave(new() { ["x"] = 1 }, false), "statistics: first save allowed");
        seconds = 14;
        check(!file.TrySave(new() { ["x"] = 2 }, false), "statistics: save throttled before fifteen seconds");
        seconds = 15;
        check(file.TrySave(new() { ["x"] = 2 }, false), "statistics: save allowed at fifteen seconds");
        check(file.TrySave(new() { ["x"] = 3 }, true), "statistics: forced save bypasses throttle");
        file.Dispose();
        check(!file.TrySave(new(), true), "statistics: disposed store cannot write");
    }

    private sealed class FakeSocket(byte[] data, int chunk, WebSocketMessageType type = WebSocketMessageType.Text) : WebSocket
    {
        private int _offset;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken token) => throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(chunk, buffer.Count), data.Length - _offset);
            data.AsSpan(_offset, count).CopyTo(buffer.AsSpan()); _offset += count;
            return Task.FromResult(new WebSocketReceiveResult(count, type, _offset == data.Length));
        }
    }
}
