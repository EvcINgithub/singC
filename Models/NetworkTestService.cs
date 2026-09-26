using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models;

public enum NetworkTestStatus { Success, Warning, Failed, Cancelled }
public enum NetworkTestKind { Local, Dns, Tcp, Http, ExitIp }

public sealed class NetworkTestRunSummary
{
    public DateTime Timestamp { get; set; }
    public string Mode { get; set; } = string.Empty;
    public int Passed { get; set; }
    public int Warnings { get; set; }
    public int Failed { get; set; }
    public string FailureText { get; set; } = string.Empty;
    public string Report { get; set; } = string.Empty;
    public string DisplayText => $"{Timestamp:MM-dd HH:mm:ss}  {Mode}  通过 {Passed} / 提醒 {Warnings} / 失败 {Failed}";
    public string FailureDisplayText => string.IsNullOrWhiteSpace(FailureText) ? "未发现失败项" : FailureText;
}

public sealed class NetworkTestResult
{
    public string Name { get; }
    public NetworkTestKind Kind { get; }
    public NetworkTestStatus Status { get; }
    public string StatusText => Status switch
    {
        NetworkTestStatus.Success => "通过",
        NetworkTestStatus.Warning => "提醒",
        NetworkTestStatus.Cancelled => "已取消",
        _ => "失败"
    };
    public string StatusGlyph => Status switch
    {
        NetworkTestStatus.Success => "\uE73E",
        NetworkTestStatus.Warning => "\uE7BA",
        NetworkTestStatus.Cancelled => "\uE71A",
        _ => "\uEA39"
    };
    public TimeSpan Duration { get; }
    public string DurationText => Duration == TimeSpan.Zero ? string.Empty : $"{Duration.TotalMilliseconds:F0} ms";
    public string Detail { get; }
    public string Advice { get; }
    public int? HttpStatusCode { get; }
    public string ReportLine => $"[{StatusText}] {Name} {DurationText}：{Detail}"
        + (string.IsNullOrEmpty(Advice) ? string.Empty : $"\n  建议：{Advice}");

    public NetworkTestResult(string name, NetworkTestKind kind, NetworkTestStatus status,
        TimeSpan duration, string detail, string advice = "", int? httpStatusCode = null)
    {
        Name = name;
        Kind = kind;
        Status = status;
        Duration = duration;
        Detail = detail;
        Advice = advice;
        HttpStatusCode = httpStatusCode;
    }
}

public sealed record NetworkTestOptions(Uri Target, Uri? ExitIp, int TimeoutSeconds = 5, bool IncludeCommonSites = true)
{
    public const string DefaultTarget = "https://www.baidu.com/";
    public const string DefaultExitIp = "https://api.ipify.org?format=json";
    public static readonly IReadOnlyList<string> CommonSites = Array.AsReadOnly(new[]
        { DefaultTarget, "https://github.com/", "https://www.google.com/" });

    public static bool TryParseUrl(string text, out Uri? uri) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    public IReadOnlyList<Uri> HttpTargets => new[] { Target }
        .Concat(IncludeCommonSites ? CommonSites.Select(url => new Uri(url)) : Array.Empty<Uri>())
        .Distinct().ToArray();
}

public sealed class NetworkTestService
{
    public const int StabilityAttempts = 10;
    private readonly Func<HttpClient> _createHttpClient;

    public NetworkTestService() : this(() => new HttpClient(new SocketsHttpHandler { UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan }) { }

    // A fresh client for each probe includes connection setup in HTTP response latency.
    internal NetworkTestService(Func<HttpClient> createHttpClient) => _createHttpClient = createHttpClient;

    public async Task RunDiagnosticsAsync(NetworkTestOptions options, bool singBoxRunning, string? configPath,
        CancellationToken token, IProgress<NetworkTestResult> progress)
    {
        token.ThrowIfCancellationRequested();
        progress.Report(new("sing-box 状态", NetworkTestKind.Local,
            singBoxRunning ? NetworkTestStatus.Success : NetworkTestStatus.Warning, TimeSpan.Zero,
            singBoxRunning ? "sing-box 正在运行。" : "sing-box 未运行，仍可检查当前系统网络。",
            "请求跟随系统代理、TUN 和路由规则；请求成功不代表一定经过代理。"));

        async Task ReportAsync(Task<NetworkTestResult> task)
        {
            var result = await task;
            token.ThrowIfCancellationRequested();
            progress.Report(result);
        }
        // Sites, the local controller and the exit service are independent probes.
        var tasks = options.HttpTargets.Select(async uri =>
        {
            var result = await ProbeHttpAsync(uri, options.TimeoutSeconds, token);
            token.ThrowIfCancellationRequested();
            progress.Report(result);
            // Diagnose the selected target and any additional site that cannot respond.
            if (uri == options.Target || result.Status == NetworkTestStatus.Failed)
                await CheckNetworkPathAsync(uri, options.TimeoutSeconds, token, progress);
        }).ToList();
        if (singBoxRunning)
            tasks.Add(ReportAsync(ExecuteAsync("控制接口", NetworkTestKind.Local, options.TimeoutSeconds,
                ct => CheckControllerAsync(configPath, ct), token)));
        if (options.ExitIp != null)
            tasks.Add(ReportAsync(ExecuteAsync("当前出口 IP", NetworkTestKind.ExitIp, options.TimeoutSeconds,
                ct => CheckExitIpAsync(options.ExitIp, ct), token)));
        await Task.WhenAll(tasks);
    }

    private static async Task CheckNetworkPathAsync(Uri uri, int timeout, CancellationToken token,
        IProgress<NetworkTestResult> progress)
    {
        async Task ReportAsync(Task<NetworkTestResult> task)
        {
            var result = await task;
            token.ThrowIfCancellationRequested();
            progress.Report(result);
        }
        await Task.WhenAll(
            ReportAsync(ExecuteAsync($"DNS · {uri.Host}", NetworkTestKind.Dns, timeout, async ct =>
            {
                var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
                return new ProbeResult(addresses.Length > 0 ? NetworkTestStatus.Success : NetworkTestStatus.Failed,
                    string.Join(", ", addresses.Select(ip => ip.ToString()).Take(4)),
                    addresses.Length > 0 ? "" : AdviceFor(NetworkTestKind.Dns));
            }, token)),
            ReportAsync(ExecuteAsync($"TCP · {uri.Host}:{uri.Port}", NetworkTestKind.Tcp, timeout, async ct =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(uri.DnsSafeHost, uri.Port, ct);
                return new ProbeResult(NetworkTestStatus.Success, $"已连接 {uri.DnsSafeHost}:{uri.Port}");
            }, token)));
    }

    public async Task RunStabilityAsync(NetworkTestOptions options, CancellationToken token,
        IProgress<NetworkTestResult> progress, TimeSpan? interval = null)
    {
        for (int i = 1; i <= StabilityAttempts; i++)
        {
            token.ThrowIfCancellationRequested();
            progress.Report(await ProbeHttpAsync(options.Target, options.TimeoutSeconds, token, $"请求 {i}/{StabilityAttempts}"));
            if (i < StabilityAttempts) await Task.Delay(interval ?? TimeSpan.FromMilliseconds(500), token);
        }
    }

    internal Task<NetworkTestResult> ProbeHttpAsync(Uri uri, int timeoutSeconds, CancellationToken token, string? name = null)
        => ExecuteAsync(name ?? uri.Host, NetworkTestKind.Http, timeoutSeconds, async ct =>
        {
            using var client = _createHttpClient();
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)response.StatusCode;
            string advice = code switch
            {
                401 or 403 => "已收到网站响应，但访问被限制；检查登录、站点策略或更换出口后重试。",
                429 => "网站限制请求频率，稍后重试或更换测试目标。",
                >= 500 => "网站或上游服务返回错误；换一个网站对照测试。",
                >= 400 => "已连接到网站，请检查网址或目标资源是否可用。",
                >= 300 => "网站返回重定向；请检查目标地址。",
                _ => ""
            };
            return new ProbeResult(response.IsSuccessStatusCode ? NetworkTestStatus.Success : NetworkTestStatus.Warning,
                $"{uri} · HTTP {code} {response.ReasonPhrase}", advice, code);
        }, token);

    private sealed record ProbeResult(NetworkTestStatus Status, string Detail, string Advice = "", int? HttpStatusCode = null);

    private static async Task<NetworkTestResult> ExecuteAsync(string name, NetworkTestKind kind, int timeoutSeconds,
        Func<CancellationToken, Task<ProbeResult>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 30)));
        try
        {
            var result = await operation(timeout.Token);
            token.ThrowIfCancellationRequested();
            return new(name, kind, result.Status, stopwatch.Elapsed, result.Detail, result.Advice, result.HttpStatusCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new(name, kind, kind == NetworkTestKind.Local ? NetworkTestStatus.Warning : NetworkTestStatus.Failed,
                stopwatch.Elapsed, $"{Math.Clamp(timeoutSeconds, 1, 30)} 秒内未响应。", AdviceFor(kind));
        }
        catch (Exception ex)
        {
            return new(name, kind, kind == NetworkTestKind.Local ? NetworkTestStatus.Warning : NetworkTestStatus.Failed,
                stopwatch.Elapsed, Trim(ex.Message, 240), AdviceFor(kind));
        }
    }

    private static string AdviceFor(NetworkTestKind kind) => kind switch
    {
        NetworkTestKind.Local => "检查当前配置是否启用了 Clash API，以及监听地址和密钥是否正确；这不等同于外网不可用。",
        NetworkTestKind.Dns => "检查域名拼写、系统 DNS 和 sing-box DNS 配置；HTTP 使用系统代理时，解析路径可能不同。",
        NetworkTestKind.Tcp => "先查看 DNS 结果，再检查目标端口、防火墙和 TUN 路由；TCP 检查不使用系统 HTTP 代理。",
        NetworkTestKind.ExitIp => "出口查询服务可能不可用；可在高级选项中更换服务，其他网站能访问时无需判定断网。",
        _ => "结合 DNS、TCP 结果检查路由、代理或证书；换一个网站测试，确认是否只有此目标失败。"
    };

    private static async Task<ProbeResult> CheckControllerAsync(string? configPath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return new(NetworkTestStatus.Warning, "未找到当前 sing-box 配置，跳过控制接口检查。");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, token));
        if (!doc.RootElement.TryGetProperty("experimental", out var experimental)
            || !experimental.TryGetProperty("clash_api", out var api)
            || !api.TryGetProperty("external_controller", out var address)
            || string.IsNullOrWhiteSpace(address.GetString()))
            return new(NetworkTestStatus.Warning, "当前配置未启用 Clash API；不影响继续测试网站连通性。");

        var builder = new UriBuilder("http://" + address.GetString());
        if (builder.Host == "0.0.0.0") builder.Host = "127.0.0.1";
        if (builder.Host is "::" or "[::]") builder.Host = "[::1]";
        builder.Path = "/version";
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        if (api.TryGetProperty("secret", out var secret) && !string.IsNullOrEmpty(secret.GetString()))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret.GetString());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return new(response.IsSuccessStatusCode ? NetworkTestStatus.Success : NetworkTestStatus.Warning,
            $"控制接口返回 HTTP {(int)response.StatusCode}",
            response.IsSuccessStatusCode ? "" : AdviceFor(NetworkTestKind.Local));
    }

    private async Task<ProbeResult> CheckExitIpAsync(Uri uri, CancellationToken token)
    {
        using var client = _createHttpClient();
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            return new(NetworkTestStatus.Warning, $"出口服务返回 HTTP {(int)response.StatusCode}", AdviceFor(NetworkTestKind.ExitIp));
        // Bound the response in case a user-configured endpoint returns a download.
        using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[4097];
        int count = 0, read;
        while (count < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(count), token)) > 0) count += read;
        string ip = count > 4096 ? "" : ExtractIp(Encoding.UTF8.GetString(buffer, 0, count));
        return string.IsNullOrEmpty(ip)
            ? new(NetworkTestStatus.Warning, "服务响应不是有效的 IPv4 / IPv6 地址。", AdviceFor(NetworkTestKind.ExitIp))
            : new(NetworkTestStatus.Success, ip, "这是该查询服务看到的出口；不同网站可能命中不同路由。");
    }

    internal static string ExtractIp(string body)
    {
        string value = body.Trim();
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("ip", out var ip)
                && ip.ValueKind == JsonValueKind.String) value = ip.GetString()?.Trim() ?? "";
        }
        catch (JsonException) { }
        bool standardNotation = value.Contains(':') || value.Split('.').Length == 4;
        return standardNotation && IPAddress.TryParse(value, out var address) ? address.ToString() : "";
    }

    public static string Summarize(IReadOnlyCollection<NetworkTestResult> results, bool stability)
    {
        if (results.Count == 0) return "尚无已完成的结果。";
        if (stability)
        {
            var requests = results.Where(r => r.Kind == NetworkTestKind.Http).ToArray();
            var successful = requests.Where(r => r.Status == NetworkTestStatus.Success).ToArray();
            string summary = $"已完成 {requests.Length}/{StabilityAttempts} 次 · 成功 {successful.Length} 次"
                + $" · 成功率 {(requests.Length == 0 ? 0 : 100d * successful.Length / requests.Length):F0}%";
            if (successful.Length > 0)
            {
                var times = successful.Select(r => r.Duration.TotalMilliseconds).ToArray();
                summary += $"\n成功请求平均 {times.Average():F0} ms · 最快 {times.Min():F0} ms · 最慢 {times.Max():F0} ms";
            }
            return summary + "\n耗时包含建立连接到收到响应头；成功率按 HTTP 2xx 计算，不代表 ICMP 丢包率或下载速度。";
        }
        var websites = results.Where(r => r.Kind == NetworkTestKind.Http).ToArray();
        if (websites.Length == 0) return "诊断进行中，等待网站测试结果。";
        int passed = websites.Count(r => r.Status == NetworkTestStatus.Success);
        string headline = $"{passed}/{websites.Length} 个网站返回成功响应。";
        if (websites.Any(r => r.Status == NetworkTestStatus.Failed))
            return headline + " 部分请求未完成，请查看失败项的排查建议。";
        if (websites.Any(r => r.Status == NetworkTestStatus.Warning))
            return headline + " 部分网站返回访问限制或服务错误，请查看 HTTP 状态。";
        return headline + (results.Any(r => r.Status != NetworkTestStatus.Success)
            ? " 另有诊断提醒，请查看下方详情。" : " 本次未发现连接异常。");
    }

    private static string Trim(string value, int maxLength)
    {
        string compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maxLength ? compact : compact[..maxLength] + "…";
    }
}
