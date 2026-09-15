using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models;

public enum NetworkTestStatus
{
    Success,
    Warning,
    Failed,
    Cancelled
}

public sealed class NetworkTestRunSummary
{
    public DateTime Timestamp { get; set; }
    public string Mode { get; set; } = string.Empty;
    public int Passed { get; set; }
    public int Warnings { get; set; }
    public int Failed { get; set; }
    public string FailureText { get; set; } = string.Empty;
    public string DisplayText => $"{Timestamp:MM-dd HH:mm:ss}  {Mode}  通过 {Passed} / 警告 {Warnings} / 失败 {Failed}";
    public string FailureDisplayText => string.IsNullOrWhiteSpace(FailureText) ? "无失败详情" : FailureText;
}

public sealed class NetworkTestResult
{
    public string Name { get; }
    public NetworkTestStatus Status { get; }
    public string StatusText => Status switch
    {
        NetworkTestStatus.Success => "通过",
        NetworkTestStatus.Warning => "警告",
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
    public string DurationText => Duration == TimeSpan.Zero ? "--" : $"{Duration.TotalMilliseconds:F0} ms";
    public string Detail { get; }
    public string ReportLine => $"[{StatusText}] {Name} ({DurationText})：{Detail}";

    public NetworkTestResult(string name, NetworkTestStatus status, TimeSpan duration, string detail)
    {
        Name = name;
        Status = status;
        Duration = duration;
        Detail = detail;
    }
}

public sealed class NetworkTestService
{
    private const int TestTimeoutSeconds = 10;
    private const string ClashApiUrl = "http://127.0.0.1:9090/version";

    public async Task<IReadOnlyList<NetworkTestResult>> RunBasicDiagnosticsAsync(
        Uri httpUri,
        string host,
        int port,
        CancellationToken cancellationToken,
        IProgress<NetworkTestResult>? progress = null)
    {
        var results = new List<NetworkTestResult>();
        results.Add(await RunAndReportAsync("Clash API", CheckClashApiAsync, cancellationToken, progress));
        results.Add(await RunAndReportAsync("DNS 解析", token => ResolveDnsAsync(host, token), cancellationToken, progress));
        results.Add(await RunAndReportAsync("TCP 端口", token => CheckTcpAsync(host, port, token), cancellationToken, progress));
        results.Add(await RunAndReportAsync("HTTP/HTTPS", token => CheckHttpAsync(httpUri, token), cancellationToken, progress));
        return results;
    }

    public async Task<IReadOnlyList<NetworkTestResult>> RunProxyDiagnosticsAsync(
        Uri httpUri,
        Uri exitIpUri,
        CancellationToken cancellationToken,
        IProgress<NetworkTestResult>? progress = null)
    {
        var results = new List<NetworkTestResult>();
        var stateResult = SingBoxService.Instance.IsRunning
            ? new NetworkTestResult("sing-box 状态", NetworkTestStatus.Success, TimeSpan.Zero, "sing-box 正在运行，开始测试 TUN 链路。")
            : new NetworkTestResult("sing-box 状态", NetworkTestStatus.Failed, TimeSpan.Zero, "sing-box 未运行，无法确认代理链路。出于安全考虑未继续请求。");
        progress?.Report(stateResult);
        results.Add(stateResult);

        if (stateResult.Status != NetworkTestStatus.Success)
            return results;

        results.Add(await RunAndReportAsync("TUN HTTP 请求", token => CheckHttpAsync(httpUri, token), cancellationToken, progress));
        results.Add(await RunAndReportAsync("代理出口 IP", token => CheckExitIpAsync(exitIpUri, token), cancellationToken, progress));
        return results;
    }

    private static async Task<NetworkTestResult> RunAndReportAsync(
        string name,
        Func<CancellationToken, Task<(NetworkTestStatus Status, string Detail)>> operation,
        CancellationToken cancellationToken,
        IProgress<NetworkTestResult>? progress)
    {
        var result = await ExecuteAsync(name, operation, cancellationToken);
        progress?.Report(result);
        return result;
    }

    private static async Task<NetworkTestResult> ExecuteAsync(
        string name,
        Func<CancellationToken, Task<(NetworkTestStatus Status, string Detail)>> operation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSeconds));

        try
        {
            var result = await operation(timeoutCts.Token);
            stopwatch.Stop();
            return new NetworkTestResult(name, result.Status, stopwatch.Elapsed, result.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new NetworkTestResult(name, NetworkTestStatus.Failed, stopwatch.Elapsed, $"超过 {TestTimeoutSeconds} 秒未完成。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new NetworkTestResult(name, NetworkTestStatus.Failed, stopwatch.Elapsed, FormatException(ex));
        }
    }

    private static async Task<(NetworkTestStatus Status, string Detail)> CheckClashApiAsync(CancellationToken cancellationToken)
    {
        using var response = await ClashWebSocketService.SharedHttpClient.GetAsync(
            ClashApiUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string detail = string.IsNullOrWhiteSpace(body)
            ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            : $"HTTP {(int)response.StatusCode}，{Trim(body, 160)}";

        return response.IsSuccessStatusCode
            ? (NetworkTestStatus.Success, detail)
            : (NetworkTestStatus.Warning, detail);
    }

    private static async Task<(NetworkTestStatus Status, string Detail)> ResolveDnsAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        string addressText = string.Join(", ", addresses.Select(address => address.ToString()).Distinct().Take(4));
        return addresses.Length == 0
            ? (NetworkTestStatus.Warning, "未返回地址记录。")
            : (NetworkTestStatus.Success, $"{addresses.Length} 个地址：{addressText}");
    }

    private static async Task<(NetworkTestStatus Status, string Detail)> CheckTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        return (NetworkTestStatus.Success, $"已连接到 {host}:{port}");
    }

    private static async Task<(NetworkTestStatus Status, string Detail)> CheckHttpAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await ClashWebSocketService.SharedHttpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var status = response.IsSuccessStatusCode ? NetworkTestStatus.Success : NetworkTestStatus.Warning;
        return (status, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static async Task<(NetworkTestStatus Status, string Detail)> CheckExitIpAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await ClashWebSocketService.SharedHttpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return (NetworkTestStatus.Warning, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        string exitIp = ExtractIp(body);
        return string.IsNullOrWhiteSpace(exitIp)
            ? (NetworkTestStatus.Warning, $"请求成功，但响应无法识别：{Trim(body, 160)}")
            : (NetworkTestStatus.Success, $"当前出口：{exitIp}");
    }

    private static string ExtractIp(string body)
    {
        string trimmed = body.Trim();
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("ip", out var ip)
                && ip.ValueKind == JsonValueKind.String)
            {
                return ip.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // 也支持接口直接返回纯文本 IP。
        }

        return IPAddress.TryParse(trimmed, out _) ? trimmed : string.Empty;
    }

    private static string FormatException(Exception exception)
    {
        Exception detail = exception is HttpRequestException && exception.InnerException != null
            ? exception.InnerException
            : exception;
        return Trim(detail.Message, 240);
    }

    private static string Trim(string value, int maxLength)
    {
        string compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}…";
    }
}
