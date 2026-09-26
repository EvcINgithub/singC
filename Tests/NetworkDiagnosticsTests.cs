using System.Net;
using System.Net.Sockets;
using singC.Models;

internal static class NetworkDiagnosticsTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request, token);
    }

    private sealed class Progress(Action<NetworkTestResult> report) : IProgress<NetworkTestResult>
    {
        public void Report(NetworkTestResult value) => report(value);
    }

    private static NetworkTestService Service(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        => new(() => new HttpClient(new Handler(respond)) { Timeout = Timeout.InfiniteTimeSpan });

    public static async Task RunAsync(Action<bool, string> check)
    {
        var target = new Uri("https://example.test:8443/path");
        check(NetworkTestOptions.TryParseUrl(target.ToString(), out var parsed) && parsed!.DnsSafeHost == "example.test"
            && parsed.Port == 8443, "network: derive DNS host and custom port from URL");
        check(!NetworkTestOptions.TryParseUrl("file:///secret", out _) && !NetworkTestOptions.TryParseUrl("https://user:pass@example.test", out _)
            && !NetworkTestOptions.TryParseUrl("invalid", out _), "network: reject unsupported URLs and URL credentials");
        check(new NetworkTestOptions(new Uri(NetworkTestOptions.DefaultTarget), null).HttpTargets.Count == 3,
            "network: deduplicate selected site from comparison targets");
        check(new NetworkTestOptions(target, null, IncludeCommonSites: false).HttpTargets.Count == 1,
            "network: disable comparison sites");
        check(NetworkTestService.ExtractIp("{\"ip\":\"not an IP\"}") == ""
            && NetworkTestService.ExtractIp("123") == ""
            && NetworkTestService.ExtractIp("{\"ip\":\"203.0.113.4\"}") == "203.0.113.4"
            && NetworkTestService.ExtractIp(" 2001:db8::1 ") == "2001:db8::1",
            "network: validate both JSON and plain-text exit IPs");

        var success = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        var ok = await success.ProbeHttpAsync(target, 5, default);
        check(ok.Status == NetworkTestStatus.Success && ok.HttpStatusCode == 204,
            "network: successful HTTP response");
        foreach (var code in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway })
        {
            var service = Service((_, _) => Task.FromResult(new HttpResponseMessage(code)));
            var result = await service.ProbeHttpAsync(target, 5, default);
            check(result.Status == NetworkTestStatus.Warning && result.HttpStatusCode == (int)code && result.Advice.Length > 0,
                $"network: HTTP {(int)code} is a website response, not a connection failure");
        }
        var failure = Service((_, _) => throw new HttpRequestException("Simulated connection error"));
        var failed = await failure.ProbeHttpAsync(target, 5, default);
        check(failed.Status == NetworkTestStatus.Failed && failed.Advice.Length > 0 && failed.HttpStatusCode == null,
            "network: connection failure has actionable advice");

        var slow = Service(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage(); });
        var timeout = await slow.ProbeHttpAsync(target, 1, default);
        check(timeout.Status == NetworkTestStatus.Failed && timeout.Detail.Contains("1 秒"),
            "network: per-probe timeout produces a result");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            try { await slow.ProbeHttpAsync(target, 5, cancel.Token); throw new Exception("Cancellation was swallowed"); }
            catch (OperationCanceledException) { check(true, "network: user cancellation propagates instead of reporting failure"); }
        }

        int requests = 0;
        var interrupted = Service((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var partial = new List<NetworkTestResult>();
        using (var cancel = new CancellationTokenSource())
        {
            var progress = new Progress(result => { partial.Add(result); if (partial.Count == 3) cancel.Cancel(); });
            try { await interrupted.RunStabilityAsync(new(target, null), cancel.Token, progress, TimeSpan.Zero); }
            catch (OperationCanceledException) { }
        }
        check(requests == 3 && partial.Count == 3, "network: cancellation retains completed samples and stops further probes");
        check(NetworkTestService.Summarize(partial, true).Contains("3/10")
            && NetworkTestService.Summarize(partial, true).Contains("100%"),
            "network: incomplete stability run reports completed denominator");
        partial.Add(failed);
        check(NetworkTestService.Summarize(partial, true).Contains("75%"),
            "network: failed requests reduce measured HTTP success rate");
        var samples = new List<NetworkTestResult>();
        await success.RunStabilityAsync(new(target, null), default, new Progress(samples.Add), TimeSpan.Zero);
        check(samples.Count == 10, "network: complete stability run performs ten probes");
        check(NetworkTestService.Summarize(new[] { ok, new NetworkTestResult("API", NetworkTestKind.Local,
            NetworkTestStatus.Warning, TimeSpan.Zero, "offline") }, false).Contains("1/1"),
            "network: local API unavailability does not mark website access as failed");

        // Full diagnostic uses loopback for DNS/TCP and fake HTTP, never public sites.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var localTarget = new Uri($"http://127.0.0.1:{port}/");
        var exitTarget = new Uri("https://exit.test/");
        var releaseExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var websiteReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<NetworkTestResult>();
        var reporting = new Progress(result =>
        {
            lock (reports) reports.Add(result);
            if (result.Kind == NetworkTestKind.Http) websiteReported.TrySetResult();
        });
        var diagnostics = Service(async (request, token) =>
        {
            if (request.RequestUri == exitTarget) await releaseExit.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ip\":\"203.0.113.9\"}") };
        });
        var run = diagnostics.RunDiagnosticsAsync(new(localTarget, exitTarget, IncludeCommonSites: false),
            true, null, default, reporting);
        await websiteReported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(!run.IsCompleted, "network: website result appears while exit lookup is still pending");
        releaseExit.SetResult();
        await run;
        check(reports.Any(r => r.Kind == NetworkTestKind.Dns && r.Status == NetworkTestStatus.Success)
            && reports.Any(r => r.Kind == NetworkTestKind.Tcp && r.Status == NetworkTestStatus.Success)
            && reports.Any(r => r.Kind == NetworkTestKind.ExitIp && r.Detail == "203.0.113.9"),
            "network: full diagnosis reports DNS, TCP and valid exit IP independently");
        check(reports.Any(r => r.Name == "控制接口" && r.Status == NetworkTestStatus.Warning),
            "network: missing controller config does not abort website checks");
    }
}
