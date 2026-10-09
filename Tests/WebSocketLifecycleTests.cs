using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using singC.Models;

internal static class WebSocketLifecycleTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var row in new[] {
            (Code:HttpStatusCode.OK,Body:"{\"UsedGB\":1,\"RemainingGB\":2,\"ResetTimestamp\":0}",Data:true,Error:false),
            (Code:HttpStatusCode.ServiceUnavailable,Body:"{}",Data:true,Error:true),
            (Code:HttpStatusCode.ServiceUnavailable,Body:"{\"error\":\"existing\"}",Data:true,Error:true),
            (Code:HttpStatusCode.OK,Body:"null",Data:false,Error:false),
            (Code:HttpStatusCode.OK,Body:"{",Data:false,Error:false),
            (Code:HttpStatusCode.InternalServerError,Body:"{}",Data:false,Error:false) })
        {
            using var client = new HttpClient(new Handler(row.Code,row.Body));
            var data = await ClashWebSocketService.FetchTrafficDataAsync(client,"http://localhost/traffic");
            check((data != null) == row.Data && (!row.Error || !string.IsNullOrEmpty(data?.Error)), "quota response " + row.Code + row.Body);
        }
        using (var client = new HttpClient(new Handler(HttpStatusCode.OK,"{}")))
            check(await ClashWebSocketService.FetchTrafficDataAsync(client,"file:///invalid") == null, "quota rejects non-HTTP URL");

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var totals = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0, accepts = 0; bool auth = true;
        async Task ServeAsync()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeout.Token); accepts++;
                using var stream = tcp.GetStream();
                var header = new StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("Unexpected EOF");
                    header.Append((char)one[0]);
                    if (header.Length > 16384) throw new IOException("Oversized request");
                }
                auth &= header.ToString().Contains("Authorization: Bearer fixture-secret", StringComparison.OrdinalIgnoreCase);
                string key = header.ToString().Split("\r\n").Single(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':',2)[1].Trim();
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
                using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
                byte[] payload = Encoding.UTF8.GetBytes("{\"uploadTotal\":12,\"downloadTotal\":34,\"connections\":[]}");
                await socket.SendAsync(new ArraySegment<byte>(payload,0,10),WebSocketMessageType.Text,false,timeout.Token);
                await socket.SendAsync(new ArraySegment<byte>(payload,10,payload.Length-10),WebSocketMessageType.Text,true,timeout.Token);
                if (attempt == 0) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"retry",timeout.Token);
                else
                {
                    try { await socket.ReceiveAsync(new ArraySegment<byte>(new byte[16]),timeout.Token); }
                    catch (WebSocketException) { }
                }
            }
        }
        var server = ServeAsync();
        using (var service = new ClashWebSocketService($"http://127.0.0.1:{port}","fixture-secret"))
        {
            service.OnTrafficTotalsReceived += (up,down) => { if (up == 12 && down == 34 && ++frames == 2) totals.TrySetResult(true); };
            await service.StartWebSocketAsync(timeout.Token);
            await service.StartWebSocketAsync(timeout.Token);
            await totals.Task.WaitAsync(timeout.Token);
            check(accepts == 2 && auth, "WebSocket reconnects to custom port with secret and fragmented frames");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await service.StopAsync();
            check(service.ConnectionState == WebSocketConnectionState.Stopped && watch.Elapsed < TimeSpan.FromSeconds(4), "WebSocket stops without peer close response");
            await service.StopAsync();
        }
        await server;
        using var failed = new ClashWebSocketService("invalid://fixture") { AutoReconnect = false };
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        failed.ConnectionStateChanged += (state,_,_) => { if(state == WebSocketConnectionState.Failed) ended.TrySetResult(true); };
        await failed.StartWebSocketAsync(timeout.Token); await ended.Task.WaitAsync(timeout.Token);
        check(failed.ReconnectAttempt == 1 && failed.LastError.Length > 0, "disabled reconnect terminates after initial failure");
        await failed.StopAsync();
    }
}
