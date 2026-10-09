using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
        await RunExpandedAsync(check);
    }

    private static async Task RunExpandedAsync(Action<bool, string> check)
    {
        foreach (var input in new[] { "http://example.test/", "https://example.test/", " HTTPS://example.test:8443/a?b=c ", "http://[::1]:8080/", "https://例子.测试/" })
            check(NetworkTestOptions.TryParseUrl(input, out var uri) && uri != null && uri.IsAbsoluteUri,
                "network contract: accepted URL " + input);
        foreach (var input in new[] { null, "", "  ", "invalid", "/relative", "ftp://example.test/", "file:///tmp/test",
            "http://", "https://user@example.test/", "https://user:pass@example.test/", "https://example.test/a\rb",
            "https://example.test/a\nb", "https://example.test/a\tb", "\thttps://example.test/", "https://example.test/\0" })
            check(!NetworkTestOptions.TryParseUrl(input!, out var uri) && uri == null,
                "network contract: invalid URL leaves no usable output " + JsonSerializer.Serialize(input));
        var selected = new Uri("https://selected.test/");
        var options = new NetworkTestOptions(selected, null);
        check(options.TimeoutSeconds == 5 && options.IncludeCommonSites && options.HttpTargets.Select(u => u.AbsoluteUri)
            .SequenceEqual(new[] { selected.AbsoluteUri, "https://www.baidu.com/", "https://github.com/", "https://www.google.com/" }),
            "network contract: default comparison targets retain selected target and order");
        check(NetworkTestOptions.DefaultExitIp == "https://api.ipify.org?format=json"
            && new NetworkTestOptions(selected, null, IncludeCommonSites: false).HttpTargets.Single() == selected,
            "network contract: default exit endpoint and disabled comparisons");

        foreach (var (body, expected) in new[] { (" 203.0.113.9 ", "203.0.113.9"), ("0.0.0.0", "0.0.0.0"),
            ("255.255.255.255", "255.255.255.255"), ("2001:0db8::1", "2001:db8::1"), ("::1", "::1"),
            ("{\"ip\":\" 203.0.113.9 \"}", "203.0.113.9"), ("{\"ip\":\"::1\"}", "::1") })
            check(NetworkTestService.ExtractIp(body) == expected, "network contract: normalized IP " + body);
        foreach (string body in new[] { "", " ", "127.1", "0x7f.0.0.1", "010.0.0.1", "1.2.3.256", "1.2.3.-1", "1.2.3.4.5",
            "1..3.4", "not:ipv6", "{}", "[]", "null", "123", "{\"ip\":null}", "{\"ip\":12}", "{\"ip\":\"\"}", "<html>error</html>" })
            check(NetworkTestService.ExtractIp(body) == "", "network contract: reject ambiguous or invalid IP " + body);

        CheckPresentation(check);
        await CheckHttpContractsAsync(check);
        await CheckDiagnosticsContractsAsync(check);
    }

    private static void CheckPresentation(Action<bool, string> check)
    {
        foreach (var (status, text, glyph) in new[] { (NetworkTestStatus.Success, "通过", "\uE73E"),
            (NetworkTestStatus.Warning, "提醒", "\uE7BA"), (NetworkTestStatus.Failed, "失败", "\uEA39"),
            (NetworkTestStatus.Cancelled, "已取消", "\uE71A"), ((NetworkTestStatus)99, "失败", "\uEA39") })
        {
            var result = new NetworkTestResult("site", NetworkTestKind.Http, status, TimeSpan.FromMilliseconds(12), "detail", "advice", 201);
            check(result.Name == "site" && result.Kind == NetworkTestKind.Http && result.Status == status && result.Detail == "detail"
                && result.Advice == "advice" && result.HttpStatusCode == 201 && result.Duration.TotalMilliseconds == 12
                && result.StatusText == text && result.StatusGlyph == glyph && result.DurationText == "12 ms"
                && result.ReportLine == $"[{text}] site 12 ms：detail\n  建议：advice", "network presentation: result contract " + status);
        }
        var plain = new NetworkTestResult("local", NetworkTestKind.Local, NetworkTestStatus.Warning, TimeSpan.Zero, "detail");
        check(plain.DurationText == "" && plain.Advice == "" && plain.HttpStatusCode == null && plain.ReportLine == "[提醒] local ：detail",
            "network presentation: zero duration and absent advice");
        var history = new NetworkTestRunSummary();
        check(history.Timestamp == default && history.Mode == "" && history.Passed == 0 && history.Warnings == 0
            && history.Failed == 0 && history.Report == "" && history.FailureText == "" && history.FailureDisplayText == "未发现失败项",
            "network presentation: empty history defaults");
        history.Timestamp = new DateTime(2026, 10, 9, 12, 34, 56);
        history.Mode = "诊断"; history.Passed = 2; history.Warnings = 3; history.Failed = 4; history.Report = "report";
        history.FailureText = "failed";
        check(history.DisplayText == "10-09 12:34:56  诊断  通过 2 / 提醒 3 / 失败 4" && history.Report == "report"
            && history.FailureDisplayText == "failed", "network presentation: serialized history values and display");
        history.FailureText = " ";
        check(history.FailureDisplayText == "未发现失败项", "network presentation: whitespace failure summary");

        NetworkTestResult Result(NetworkTestKind kind, NetworkTestStatus status, int milliseconds = 10)
            => new("sample", kind, status, TimeSpan.FromMilliseconds(milliseconds), "detail");
        check(NetworkTestService.Summarize([], false) == "尚无已完成的结果。"
            && NetworkTestService.Summarize([plain], false) == "诊断进行中，等待网站测试结果。", "network summary: empty and pending");
        var good = Result(NetworkTestKind.Http, NetworkTestStatus.Success);
        check(NetworkTestService.Summarize([good], false) == "1/1 个网站返回成功响应。 本次未发现连接异常。", "network summary: all websites succeed");
        check(NetworkTestService.Summarize([good, plain], false) == "1/1 个网站返回成功响应。 另有诊断提醒，请查看下方详情。", "network summary: ancillary warnings");
        check(NetworkTestService.Summarize([good, Result(NetworkTestKind.Http, NetworkTestStatus.Warning)], false)
            == "1/2 个网站返回成功响应。 部分网站返回访问限制或服务错误，请查看 HTTP 状态。", "network summary: website warning");
        check(NetworkTestService.Summarize([good, Result(NetworkTestKind.Http, NetworkTestStatus.Failed), Result(NetworkTestKind.Http, NetworkTestStatus.Warning)], false)
            == "1/3 个网站返回成功响应。 部分请求未完成，请查看失败项的排查建议。", "network summary: failure takes priority");
        const string suffix = "\n耗时包含建立连接到收到响应头；成功率按 HTTP 2xx 计算，不代表 ICMP 丢包率或下载速度。";
        check(NetworkTestService.Summarize([plain], true) == "已完成 0/10 次 · 成功 0 次 · 成功率 0%" + suffix,
            "network summary: no HTTP samples has no division by zero");
        check(NetworkTestService.Summarize([Result(NetworkTestKind.Http, NetworkTestStatus.Failed)], true)
            == "已完成 1/10 次 · 成功 0 次 · 成功率 0%" + suffix, "network summary: no successes omit latency stats");
        check(NetworkTestService.Summarize([good, Result(NetworkTestKind.Http, NetworkTestStatus.Success, 30),
            Result(NetworkTestKind.Http, NetworkTestStatus.Warning, 999), plain], true)
            == "已完成 3/10 次 · 成功 2 次 · 成功率 67%\n成功请求平均 20 ms · 最快 10 ms · 最慢 30 ms" + suffix,
            "network summary: latency uses only successful HTTP samples");
    }

    private static async Task CheckHttpContractsAsync(Action<bool, string> check)
    {
        var target = new Uri("https://example.test/path");
        foreach (var (code, advice) in new[] { (200, ""), (299, ""), (300, "网站返回重定向；请检查目标地址。"),
            (399, "网站返回重定向；请检查目标地址。"), (400, "已连接到网站，请检查网址或目标资源是否可用。"),
            (401, "已收到网站响应，但访问被限制；检查登录、站点策略或更换出口后重试。"),
            (403, "已收到网站响应，但访问被限制；检查登录、站点策略或更换出口后重试。"),
            (429, "网站限制请求频率，稍后重试或更换测试目标。"), (499, "已连接到网站，请检查网址或目标资源是否可用。"),
            (500, "网站或上游服务返回错误；换一个网站对照测试。"), (599, "网站或上游服务返回错误；换一个网站对照测试。") })
        {
            var content = new TrackedContent("body");
            var service = Service((request, _) =>
            {
                check(request.Method == HttpMethod.Get && request.RequestUri == target, "network HTTP: method and target");
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = content, ReasonPhrase = "test" });
            });
            var result = await service.ProbeHttpAsync(target, 5, default, "custom");
            check(result.Status == (code < 300 ? NetworkTestStatus.Success : NetworkTestStatus.Warning) && result.Name == "custom"
                && result.Kind == NetworkTestKind.Http && result.HttpStatusCode == code && result.Advice == advice
                && result.Detail == $"{target} · HTTP {code} test" && content.Disposed && !content.Serialized,
                "network HTTP: status boundary, headers-only, and disposal " + code);
        }
        foreach (int input in new[] { int.MinValue, 0, 1, 30, 31, int.MaxValue })
        {
            var service = Service((_, _) => throw new OperationCanceledException());
            var result = await service.ProbeHttpAsync(target, input, default);
            check(result.Status == NetworkTestStatus.Failed && result.Detail == $"{Math.Clamp(input, 1, 30)} 秒内未响应。"
                && result.Advice.Contains("DNS、TCP") && result.HttpStatusCode == null, "network HTTP: normalized timeout " + input);
        }
        foreach (int length in new[] { 239, 240, 241 })
        {
            string message = new('x', length);
            var result = await Service((_, _) => throw new HttpRequestException("\r\n " + message + " \n"))
                .ProbeHttpAsync(target, 5, default);
            check(result.Name == "example.test" && result.Detail == (length <= 240 ? message : message[..240] + "…")
                && result.Status == NetworkTestStatus.Failed && result.Advice.Contains("证书"), "network HTTP: error trimming boundary " + length);
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        int calls = 0;
        var preCancelled = Service((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage()); });
        await MustCancel(() => preCancelled.ProbeHttpAsync(target, 5, cancel.Token), check, "pre-cancelled HTTP");
        await MustCancel(() => preCancelled.RunDiagnosticsAsync(new(target, null), false, null, cancel.Token,
            new Progress(_ => calls++)), check, "pre-cancelled diagnosis");
        await MustCancel(() => preCancelled.RunStabilityAsync(new(target, null), cancel.Token,
            new Progress(_ => calls++)), check, "pre-cancelled stability");
        check(calls == 0, "network cancellation: pre-cancelled operations have no side effects");
        using var during = new CancellationTokenSource();
        var late = Service((_, _) => { during.Cancel(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        await MustCancel(() => late.ProbeHttpAsync(target, 5, during.Token), check, "cancellation wins over completed response");
        using var delayCancel = new CancellationTokenSource();
        var samples = new List<NetworkTestResult>();
        var immediate = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await MustCancel(() => immediate.RunStabilityAsync(new(target, null), delayCancel.Token, new Progress(result =>
        { samples.Add(result); delayCancel.CancelAfter(30); })), check, "default stability interval can be cancelled");
        check(samples.Count == 1 && samples[0].Name == "请求 1/10", "network stability: default interval prevents a tight loop");
        using var finalCancel = new CancellationTokenSource();
        var completed = new List<NetworkTestResult>();
        await immediate.RunStabilityAsync(new(target, null), finalCancel.Token, new Progress(result =>
        { completed.Add(result); if (completed.Count == 10) finalCancel.Cancel(); }), TimeSpan.Zero);
        check(completed.Select(r => r.Name).SequenceEqual(Enumerable.Range(1, 10).Select(i => $"请求 {i}/10")),
            "network stability: all sample names and no cancellable delay after final sample");
    }

    private sealed class TrackedContent(string body) : HttpContent
    {
        public bool Disposed { get; private set; }
        public bool Serialized { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Serialized = true; return stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask(); }
        protected override bool TryComputeLength(out long length) { length = Encoding.UTF8.GetByteCount(body); return true; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task MustCancel(Func<Task> action, Action<bool, string> check, string name)
    {
        try { await action(); throw new Exception("Expected cancellation: " + name); }
        catch (OperationCanceledException) { check(true, "network cancellation: " + name); }
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int ReadCalls { get; private set; }
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            ReadCalls++;
            int read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], token);
            BytesRead += read;
            return read;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static async Task CheckDiagnosticsContractsAsync(Action<bool, string> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var acceptStop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try { while (true) { using var socket = await listener.AcceptTcpClientAsync(acceptStop.Token); } }
            catch (OperationCanceledException) { }
        });
        var target = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var exit = new Uri("https://exit.test/");
        string directory = Path.Combine(Path.GetTempPath(), "singC-network-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            async Task<List<NetworkTestResult>> Diagnose(NetworkTestService service, bool running = false, string? path = null,
                Uri? exitUri = null, bool common = false, int timeout = 5)
            {
                var results = new List<NetworkTestResult>();
                await service.RunDiagnosticsAsync(new(target, exitUri, timeout, common), running, path, default,
                    new Progress(result => { lock (results) results.Add(result); }));
                return results;
            }
            var good = Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            var stopped = await Diagnose(good, common: true);
            var state = stopped.Single(r => r.Name == "sing-box 状态");
            check(state.Status == NetworkTestStatus.Warning && state.Detail == "sing-box 未运行，仍可检查当前系统网络。"
                && state.Advice == "请求跟随系统代理、TUN 和路由规则；请求成功不代表一定经过代理。"
                && state.Duration == TimeSpan.Zero && !stopped.Any(r => r.Name == "控制接口" || r.Kind == NetworkTestKind.ExitIp)
                && stopped.Count(r => r.Kind == NetworkTestKind.Http) == 4 && stopped.Count(r => r.Kind == NetworkTestKind.Dns) == 1
                && stopped.Count(r => r.Kind == NetworkTestKind.Tcp) == 1, "network diagnosis: stopped core and independent comparison probes");
            var dns = stopped.Single(r => r.Kind == NetworkTestKind.Dns);
            var tcp = stopped.Single(r => r.Kind == NetworkTestKind.Tcp);
            check(dns.Name == "DNS · 127.0.0.1" && dns.Detail == "127.0.0.1" && dns.Advice == ""
                && dns.Status == NetworkTestStatus.Success && tcp.Name == $"TCP · 127.0.0.1:{target.Port}"
                && tcp.Detail == $"已连接 127.0.0.1:{target.Port}" && tcp.Advice == "" && tcp.Status == NetworkTestStatus.Success,
                "network diagnosis: DNS and TCP result content and advice");
            using (var bound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                bound.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Reserved port, deliberately not listening.
                var closedTarget = new Uri($"http://127.0.0.1:{((IPEndPoint)bound.LocalEndPoint!).Port}/");
                var failedPath = new List<NetworkTestResult>();
                await good.RunDiagnosticsAsync(new(closedTarget, null, IncludeCommonSites: false), false, null, default,
                    new Progress(result => { lock (failedPath) failedPath.Add(result); }));
                var failedTcp = failedPath.Single(r => r.Kind == NetworkTestKind.Tcp);
                check(failedTcp.Status == NetworkTestStatus.Failed && failedTcp.Advice.Contains("TCP 检查不使用系统 HTTP 代理"),
                    "network diagnosis: closed TCP port is actually probed");
            }
            foreach (string? path in new[] { null, " ", Path.Combine(directory, "missing.json") })
            {
                var results = await Diagnose(good, true, path);
                check(results.Single(r => r.Name == "sing-box 状态").Status == NetworkTestStatus.Success
                    && results.Single(r => r.Name == "sing-box 状态").Detail == "sing-box 正在运行。"
                    && results.Single(r => r.Name == "控制接口").Detail == "未找到当前 sing-box 配置，跳过控制接口检查。",
                    "network controller: absent configuration " + path);
            }
            foreach (string json in new[] { "{}", "{\"experimental\":{}}", "{\"experimental\":{\"clash_api\":{}}}",
                "{\"experimental\":{\"clash_api\":{\"external_controller\":\" \"}}}" })
            {
                string file = Path.Combine(directory, "disabled.json");
                await File.WriteAllTextAsync(file, json);
                var result = (await Diagnose(good, true, file)).Single(r => r.Name == "控制接口");
                check(result.Status == NetworkTestStatus.Warning && result.Detail == "当前配置未启用 Clash API；不影响继续测试网站连通性。",
                    "network controller: disabled field combination " + json);
            }
            foreach (string json in new[] { "not json", "null", "{\"experimental\":null}", "{\"experimental\":{\"clash_api\":7}}",
                "{\"experimental\":{\"clash_api\":{\"external_controller\":7}}}" })
            {
                string file = Path.Combine(directory, "invalid.json");
                await File.WriteAllTextAsync(file, json);
                var result = (await Diagnose(good, true, file)).Single(r => r.Name == "控制接口");
                check(result.Status == NetworkTestStatus.Warning && result.Detail.Length > 0 && result.Advice.Contains("密钥"),
                    "network controller: malformed configuration is a warning " + json);
            }
            foreach (var (host, code, secret) in new[] { ("127.0.0.1", 200, (string?)null), ("0.0.0.0", 401, "token"),
                ("127.0.0.1", 302, ""), ("[::]", 200, (string?)null) })
                await CheckControllerResponseAsync(target, directory, host, code, secret, check);

            foreach (var (body, expected) in new[] { ("203.0.113.8", NetworkTestStatus.Success), ("::1", NetworkTestStatus.Success),
                ("{\"ip\":\"203.0.113.8\"}", NetworkTestStatus.Success), ("", NetworkTestStatus.Warning), ("{}", NetworkTestStatus.Warning),
                ("203.0.113.8".PadRight(4096), NetworkTestStatus.Success), ("203.0.113.8".PadRight(4097), NetworkTestStatus.Warning) })
            {
                var results = await Diagnose(Service((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(request.RequestUri == exit ? body : "") })), exitUri: exit);
                var result = results.Single(r => r.Kind == NetworkTestKind.ExitIp);
                check(result.Name == "当前出口 IP" && result.Status == expected && result.Advice.Length > 0
                    && (expected == NetworkTestStatus.Success ? result.Detail == (body.Contains(':') && !body.StartsWith('{') ? "::1" : "203.0.113.8")
                        : result.Detail == "服务响应不是有效的 IPv4 / IPv6 地址。"), "network exit: payload and length boundary " + body.Length);
            }
            var unavailable = (await Diagnose(Service((request, _) => Task.FromResult(new HttpResponseMessage(
                request.RequestUri == exit ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))), exitUri: exit))
                .Single(r => r.Kind == NetworkTestKind.ExitIp);
            check(unavailable.Status == NetworkTestStatus.Warning && unavailable.Detail == "出口服务返回 HTTP 503"
                && unavailable.Advice.Contains("出口查询服务"), "network exit: non-success response does not mean offline");
            foreach (int length in new[] { 4096, 8192 })
            {
                var stream = new ChunkedStream(Encoding.UTF8.GetBytes("203.0.113.8".PadRight(length)));
                var result = (await Diagnose(Service((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = request.RequestUri == exit ? new StreamContent(stream) : new StringContent("") })), exitUri: exit))
                    .Single(r => r.Kind == NetworkTestKind.ExitIp);
                check(result.Status == (length == 4096 ? NetworkTestStatus.Success : NetworkTestStatus.Warning)
                    && stream.ReadCalls > 1 && stream.BytesRead == Math.Min(length, 4097) && stream.Disposed,
                    "network exit: fragmented response stays bounded and stream is disposed " + length);
            }
            await CheckDefaultClientAsync(check);
        }
        finally
        {
            acceptStop.Cancel();
            await accepting;
            Directory.Delete(directory, true);
        }
    }

    private static async Task CheckDefaultClientAsync(Action<bool, string> check)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serving = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? requestLine = await reader.ReadLineAsync(deadline.Token);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"), deadline.Token);
            return requestLine;
        });
        try
        {
            var result = await new NetworkTestService().ProbeHttpAsync(new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/probe"), 5, deadline.Token);
            check(result.Status == NetworkTestStatus.Success && result.HttpStatusCode == 204
                && await serving == "GET /probe HTTP/1.1", "network HTTP: production client factory serves loopback request");
        }
        finally
        {
            deadline.Cancel();
            try { await serving; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CheckControllerResponseAsync(Uri target, string directory, string host, int code, string? secret,
        Action<bool, string> check)
    {
        using var server = new TcpListener(host == "[::]" ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
        server.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool ipv6AccessDenied = false;
        if (host == "[::]")
        {
            // Some TUN/host policies block even IPv6 loopback. Distinguish that from a service regression.
            try
            {
                using var probe = new TcpClient(AddressFamily.InterNetworkV6);
                await probe.ConnectAsync(IPAddress.IPv6Loopback, ((IPEndPoint)server.LocalEndpoint).Port, deadline.Token);
                using var accepted = await server.AcceptTcpClientAsync(deadline.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
            { ipv6AccessDenied = true; }
        }
        var receive = ipv6AccessDenied ? Task.FromResult(new List<string>()) : Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            var headers = new List<string>();
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } line) headers.Add(line);
            string location = code == 302 ? $"Location: {target}\r\n" : "";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Test\r\n{location}Content-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
            return headers;
        });
        string file = Path.Combine(directory, "controller.json");
        var api = new Dictionary<string, object?> { ["external_controller"] = $"{host}:{((IPEndPoint)server.LocalEndpoint).Port}" };
        if (secret != null) api["secret"] = secret;
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { experimental = new { clash_api = api } }));
        var results = new List<NetworkTestResult>();
        try
        {
            await Service((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
                .RunDiagnosticsAsync(new(target, null, IncludeCommonSites: false), true, file, deadline.Token,
                    new Progress(result => { lock (results) results.Add(result); }));
            var result = results.Single(r => r.Name == "控制接口");
            if (ipv6AccessDenied)
            {
                check(result.Status == NetworkTestStatus.Warning && result.Detail.Contains("[::1]") && result.Advice.Contains("Clash API"),
                    "network controller: IPv6 loopback access denied by host; normalized address and warning verified, connectivity unavailable");
                return;
            }
            check(result.Status == (code == 200 ? NetworkTestStatus.Success : NetworkTestStatus.Warning)
                && result.Detail == $"控制接口返回 HTTP {code}" && (code == 200 ? result.Advice == "" : result.Advice.Contains("Clash API")),
                "network controller: response " + host + " " + code + " received " + result.Detail);
            var headers = await receive;
            check(headers[0] == "GET /version HTTP/1.1" && (string.IsNullOrEmpty(secret)
                ? !headers.Any(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                : headers.Contains("Authorization: Bearer " + secret)), "network controller: request path and authentication " + host);
        }
        finally
        {
            deadline.Cancel();
            try { await receive; } catch (OperationCanceledException) { }
        }
    }
}
