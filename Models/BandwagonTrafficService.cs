using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using singC.Helpers;
using singC.Updates;

namespace singC.Models;

public sealed record BandwagonApiQuota(long Remaining15Minutes, long Remaining24Hours);
public sealed record BandwagonQuotaResponse(BandwagonApiQuota? Quota, string? Error = null, long RetryAfterSeconds = 0);

public sealed class BandwagonTrafficService(HttpClient client)
{
    internal static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
    private static readonly HttpClient DefaultClient = new(CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    public static BandwagonTrafficService Instance { get; } = new(DefaultClient);
    public Task<TrafficData> QueryAsync(BandwagonCredentials credentials, CancellationToken token = default) =>
        SendAsync(credentials, "getServiceInfo", Parse,
            (error, retry) => new TrafficData { Error = error, RetryAfterSeconds = retry }, token);

    public Task<BandwagonQuotaResponse> QueryQuotaAsync(BandwagonCredentials credentials, CancellationToken token = default) =>
        SendAsync(credentials, "getRateLimitStatus", ParseQuota,
            (error, retry) => new BandwagonQuotaResponse(null, error, retry), token);

    private async Task<T> SendAsync<T>(BandwagonCredentials credentials, string action,
        Func<string, T> parse, Func<string, long, T> failure, CancellationToken token)
    {
        // Keep credentials out of URLs, diagnostics and server-supplied error messages.
        credentials = BandwagonCredentials.Create(credentials.Veid, credentials.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.64clouds.com/v1/" + action)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["veid"] = credentials.Veid, ["api_key"] = credentials.ApiKey
                })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                return failure("搬瓦工 API 暂缓查询，请稍后重试。", RetryDelay(response));
            if (!response.IsSuccessStatusCode)
                return failure("搬瓦工 API 请求失败，请检查凭据或网络。", 0);
            string json = await BoundedResponse.ReadAsync(response.Content, 1024 * 1024, timeout.Token);
            return parse(json);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return failure("搬瓦工查询超时，请检查网络。", 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or OverflowException or FormatException or KeyNotFoundException)
        {
            return failure("搬瓦工查询失败，请检查网络或稍后重试。", 0);
        }
    }

    private static long RetryDelay(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        double seconds = (retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow))?.TotalSeconds ?? 900;
        return (long)Math.Clamp(seconds, 60, 86400);
    }

    internal static BandwagonQuotaResponse ParseQuota(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ReadInteger(root, "error") != 0)
            return new(null, "搬瓦工 API 拒绝配额查询，请检查凭据和访问权限。");
        long shortTerm = ReadInteger(root, "remaining_points_15min");
        long daily = ReadInteger(root, "remaining_points_24h");
        if (shortTerm < 0 || daily < 0) throw new InvalidDataException("Invalid API quota");
        return new(new BandwagonApiQuota(shortTerm, daily));
    }

    internal static TrafficData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (ReadInteger(root, "error") != 0)
            return new TrafficData { Error = "搬瓦工 API 拒绝查询，请检查 VEID、API Key 和访问权限。" };
        double used = ReadNumber(root, "data_counter");
        double total = ReadNumber(root, "plan_monthly_data");
        double multiplier = ReadNumber(root, "monthly_data_multiplier");
        long reset = ReadInteger(root, "data_next_reset");
        if (!double.IsFinite(used) || !double.IsFinite(total) || !double.IsFinite(multiplier)
            || used < 0 || total < 0 || multiplier <= 0)
            throw new InvalidDataException("Invalid bandwidth counters");
        const double gigabyte = 1024d * 1024 * 1024;
        var result = new TrafficData
        {
            UsedGB = used * multiplier / gigabyte,
            RemainingGB = Math.Max(0, total - used) * multiplier / gigabyte,
            ResetTimestamp = reset
        };
        if (!result.HasTrafficData) throw new InvalidDataException("Invalid bandwidth data");
        return result;
    }

    private static double ReadNumber(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? double.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture) : value.GetDouble();
    }

    private static long ReadInteger(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture) : value.GetInt64();
    }
}
