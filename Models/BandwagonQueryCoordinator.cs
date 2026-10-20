using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using singC.Helpers;

namespace singC.Models;

public sealed record BandwagonRefreshResult(TrafficData? Traffic, BandwagonApiQuota? Quota,
    DateTimeOffset? TrafficUpdatedAt, DateTimeOffset? QuotaCheckedAt,
    DateTimeOffset NextAutomaticAt, DateTimeOffset NextManualAt, DateTimeOffset NextQuotaAt,
    string Status, string QuotaStatus, bool Cached = false);

// All production refresh paths share this gate and per-credential cooldowns.
// No API point cost or provider reset boundary is assumed.
public sealed class BandwagonQueryCoordinator(
    Func<BandwagonCredentials, CancellationToken, Task<TrafficData>> queryTraffic,
    Func<BandwagonCredentials, CancellationToken, Task<BandwagonQuotaResponse>> queryQuota,
    Func<DateTimeOffset> clock)
{
    public static BandwagonQueryCoordinator Instance { get; } = new(
        BandwagonTrafficService.Instance.QueryAsync, BandwagonTrafficService.Instance.QueryQuotaAsync, () => DateTimeOffset.UtcNow);
    private sealed class Entry
    {
        public BandwagonRefreshResult Result = new(null, null, null, null,
            DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue, "", "");
        public DateTimeOffset BlockTrafficUntil;
    }
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string Key(BandwagonCredentials credentials) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.Veid + "\0" + credentials.ApiKey)));

    private Entry GetEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new Entry();
        return entry;
    }

    public async Task<BandwagonRefreshResult> RefreshTrafficAsync(BandwagonCredentials credentials,
        bool manual = false, CancellationToken token = default)
    {
        credentials = BandwagonCredentials.Create(credentials.Veid, credentials.ApiKey);
        await _gate.WaitAsync(token);
        try
        {
            var entry = GetEntry(Key(credentials));
            var now = clock();
            var previous = entry.Result;
            if (now < previous.NextManualAt || (!manual && now < previous.NextAutomaticAt))
                return previous with { Cached = true };
            if (now < entry.BlockTrafficUntil)
                return Finish(entry, "已知 API 点数耗尽，套餐查询暂缓。", now, entry.BlockTrafficUntil - now);
            // Reserve cooldowns even if a request is cancelled or fails unexpectedly.
            entry.Result = previous with { NextAutomaticAt = now.AddHours(8), NextManualAt = now.AddMinutes(15), Status = "查询未完成，请稍后重试。", Cached = false };
            var traffic = await queryTraffic(credentials, token);
            now = clock();
            if (!traffic.HasTrafficData)
            {
                Finish(entry, traffic.Error ?? "套餐查询失败，稍后重试。", now,
                    TimeSpan.FromSeconds(Math.Clamp(Math.Max(900, traffic.RetryAfterSeconds), 900, 86400)));
                RespectRetry(entry, now, traffic.RetryAfterSeconds);
                return entry.Result;
            }

            return entry.Result = entry.Result with
            {
                Traffic = traffic, TrafficUpdatedAt = now, NextAutomaticAt = now.AddHours(8),
                NextManualAt = now.AddMinutes(1), Status = "", Cached = false
            };
        }
        finally { _gate.Release(); }
    }

    // Explicit user action only: traffic refresh never calls this endpoint.
    public async Task<BandwagonRefreshResult> RefreshQuotaAsync(BandwagonCredentials credentials, CancellationToken token = default)
    {
        credentials = BandwagonCredentials.Create(credentials.Veid, credentials.ApiKey);
        await _gate.WaitAsync(token);
        try
        {
            var entry = GetEntry(Key(credentials));
            var now = clock();
            if (now < entry.Result.NextQuotaAt) return entry.Result with { Cached = true };
            entry.Result = entry.Result with { NextQuotaAt = now.AddMinutes(15), QuotaStatus = "配额查询未完成，请稍后重试。", Cached = false };
            var response = await queryQuota(credentials, token);
            now = clock();
            if (response.Quota == null)
            {
                entry.Result = entry.Result with
                {
                    NextQuotaAt = now.AddSeconds(Math.Clamp(Math.Max(900, response.RetryAfterSeconds), 900, 86400)),
                    QuotaStatus = response.Error ?? "API 配额查询失败，请稍后重试。"
                };
                RespectRetry(entry, now, response.RetryAfterSeconds);
                return entry.Result;
            }
            var quota = response.Quota;
            bool previouslyBlocked = entry.BlockTrafficUntil > now;
            entry.BlockTrafficUntil = quota.Remaining24Hours == 0 ? now.AddHours(24)
                : quota.Remaining15Minutes == 0 ? now.AddMinutes(15) : DateTimeOffset.MinValue;
            if (previouslyBlocked && entry.BlockTrafficUntil == DateTimeOffset.MinValue)
                entry.Result = entry.Result with { NextManualAt = now, NextAutomaticAt = now.AddHours(8), Status = "" };
            return entry.Result = entry.Result with
            {
                Quota = quota, QuotaCheckedAt = now, NextQuotaAt = now.AddMinutes(1),
                QuotaStatus = entry.BlockTrafficUntil > now ? "API 点数耗尽，套餐查询暂缓；稍后可手动核对余量。" : ""
            };
        }
        finally { _gate.Release(); }
    }

    private static BandwagonRefreshResult Finish(Entry entry, string status, DateTimeOffset now, TimeSpan delay) =>
        entry.Result = entry.Result with { Status = status, NextAutomaticAt = now.Add(delay > TimeSpan.FromHours(8) ? delay : TimeSpan.FromHours(8)), NextManualAt = now.Add(delay), Cached = false };

    private static void RespectRetry(Entry entry, DateTimeOffset now, long retry)
    {
        if (retry <= 0) return;
        var until = now.AddSeconds(Math.Clamp(Math.Max(900, retry), 900, 86400));
        var result = entry.Result;
        var automatic = result.NextAutomaticAt == DateTimeOffset.MinValue ? now.AddHours(8) : result.NextAutomaticAt;
        entry.Result = result with
        {
            NextManualAt = result.NextManualAt > until ? result.NextManualAt : until,
            NextQuotaAt = result.NextQuotaAt > until ? result.NextQuotaAt : until,
            NextAutomaticAt = automatic > until ? automatic : until
        };
    }
}
