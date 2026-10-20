using System.Net;
using System.Net.Http.Headers;
using singC.Helpers;
using singC.Models;

internal static class BandwagonQuotaTests
{
    private static readonly BandwagonCredentials Credentials = new("123", "fixture-key");
    private static TrafficData ValidTraffic() => new() { UsedGB = 1, RemainingGB = 2, ResetTimestamp = 1700000000 };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public int TrafficCalls, QuotaCalls;
        public TrafficData Traffic = ValidTraffic();
        public BandwagonQuotaResponse Quota = new(new(100, 1000));
        public Task<TrafficData>? PendingTraffic;
        public BandwagonQueryCoordinator Coordinator { get; }
        public Harness()
        {
            Coordinator = new(async (_, token) =>
            {
                TrafficCalls++;
                return PendingTraffic == null ? Traffic : await PendingTraffic.WaitAsync(token);
            }, (_, _) => { QuotaCalls++; return Task.FromResult(Quota); }, () => Now);
        }
    }

    public static Task RunAsync(Action<bool, string> check) => RunCoreAsync(check).WaitAsync(TimeSpan.FromSeconds(3));

    private static async Task RunCoreAsync(Action<bool, string> check)
    {
        async Task<BandwagonQuotaResponse> Query(string json, HttpStatusCode status = HttpStatusCode.OK, long retry = 0)
        {
            using var client = new HttpClient(new Handler(async (request, token) =>
            {
                check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://api.64clouds.com/v1/getRateLimitStatus", "API quota fixed POST endpoint");
                check(await request.Content!.ReadAsStringAsync(token) == "veid=123&api_key=fixture-key", "API quota credentials in body");
                var response = new HttpResponseMessage(status) { Content = new StringContent(json) };
                if (retry > 0) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retry));
                return response;
            }));
            return await new BandwagonTrafficService(client).QueryQuotaAsync(Credentials);
        }
        var parsed = await Query("{\"error\":0,\"remaining_points_15min\":10,\"remaining_points_24h\":20}");
        check(parsed.Quota == new BandwagonApiQuota(10, 20) && parsed.Error == null && parsed.RetryAfterSeconds == 0, "API quota parses both point windows");
        parsed = await Query("{\"error\":\"0\",\"remaining_points_15min\":\"0\",\"remaining_points_24h\":\"9223372036854775807\"}");
        check(parsed.Quota == new BandwagonApiQuota(0, long.MaxValue), "API quota accepts numeric strings zero and maximum");
        parsed = await Query("{\"error\":0,\"remaining_points_15min\":1,\"remaining_points_24h\":0}");
        check(parsed.Quota == new BandwagonApiQuota(1, 0), "API quota reports zero daily balance as valid exhaustion");
        foreach (string json in new[] { "{}", "null", "[]", "{", "{\"error\":0,\"remaining_points_15min\":1}",
            "{\"error\":0,\"remaining_points_15min\":-1,\"remaining_points_24h\":1}",
            "{\"error\":0,\"remaining_points_15min\":1,\"remaining_points_24h\":-1}",
            "{\"error\":0,\"remaining_points_15min\":1.5,\"remaining_points_24h\":1}",
            "{\"error\":0,\"remaining_points_15min\":true,\"remaining_points_24h\":1}",
            "{\"error\":0,\"remaining_points_15min\":\"99999999999999999999\",\"remaining_points_24h\":1}" })
        {
            var result = await Query(json);
            check(result.Quota == null && !string.IsNullOrEmpty(result.Error), "API quota rejects malformed response");
        }
        var rejected = await Query("{\"error\":-1,\"message\":\"fixture-key\"}");
        check(rejected.Quota == null && rejected.Error!.Contains("拒绝") && !rejected.Error.Contains("fixture-key"), "API quota redacts server error");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect, HttpStatusCode.InternalServerError })
        {
            var failed = await Query("secret", status);
            check(failed.Quota == null && !string.IsNullOrEmpty(failed.Error), "API quota HTTP failure " + status);
        }
        var limited = await Query("secret", HttpStatusCode.TooManyRequests, 1800);
        check(limited.Quota == null && limited.RetryAfterSeconds == 1800 && !string.IsNullOrEmpty(limited.Error), "API quota honors Retry-After");
        using (var client = new HttpClient(new Handler((_, _) => throw new HttpRequestException("fixture-key"))))
        {
            var failed = await new BandwagonTrafficService(client).QueryQuotaAsync(Credentials);
            check(failed.Quota == null && !failed.Error!.Contains("fixture-key"), "API quota redacts transport error");
        }

        var h = new Harness(); var start = h.Now;
        var first = await h.Coordinator.RefreshTrafficAsync(Credentials);
        check(h.TrafficCalls == 1 && h.QuotaCalls == 0 && first.Quota == null && first.QuotaStatus == "", "traffic never implicitly queries API quota");
        check(first.Traffic == h.Traffic && first.TrafficUpdatedAt == start && first.NextAutomaticAt == start.AddHours(8)
            && first.NextManualAt == start.AddMinutes(1) && first.Status == "" && !first.Cached, "traffic eight-hour automatic and one-minute manual schedule");
        var cached = await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        check(cached.Cached && h.TrafficCalls == 1 && cached.TrafficUpdatedAt == start, "repeated manual traffic uses cache without timestamp inflation");
        h.Now = start.AddMinutes(1);
        check((await h.Coordinator.RefreshTrafficAsync(Credentials)).Cached && h.TrafficCalls == 1, "automatic traffic waits eight hours despite manual eligibility");
        check(!(await h.Coordinator.RefreshTrafficAsync(Credentials, true)).Cached && h.TrafficCalls == 2, "manual traffic allowed at one-minute boundary");
        h.Now = start.AddMinutes(1).AddHours(8);
        await h.Coordinator.RefreshTrafficAsync(Credentials);
        check(h.TrafficCalls == 3 && h.QuotaCalls == 0, "automatic traffic resumes at eight-hour boundary without quota request");
        var trafficTime = h.Now;
        var quota = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(h.TrafficCalls == 3 && h.QuotaCalls == 1 && quota.Quota == new BandwagonApiQuota(100, 1000)
            && quota.QuotaCheckedAt == h.Now && quota.TrafficUpdatedAt == trafficTime && quota.QuotaStatus == "", "manual API quota independent of traffic and timestamp");
        check((await h.Coordinator.RefreshQuotaAsync(Credentials)).Cached && h.QuotaCalls == 1, "manual quota one-minute cache");
        h.Now = h.Now.AddMinutes(1);
        await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(h.QuotaCalls == 2 && h.TrafficCalls == 3, "manual quota resumes at cooldown boundary only");

        h = new(); var quotaOnly = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(quotaOnly.Traffic == null && quotaOnly.Status == "" && quotaOnly.NextAutomaticAt == DateTimeOffset.MinValue, "quota-only result does not invent traffic state or schedule");
        h = new(); var scheduled = await h.Coordinator.RefreshTrafficAsync(Credentials);
        h.Now = h.Now.AddMinutes(10);
        var quotaWithoutPause = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(quotaWithoutPause.NextAutomaticAt == scheduled.NextAutomaticAt && quotaWithoutPause.NextManualAt == scheduled.NextManualAt, "positive quota does not reset unrelated traffic schedule");

        foreach (var points in new[] { new BandwagonApiQuota(0, 10), new BandwagonApiQuota(10, 0), new BandwagonApiQuota(0, 0) })
        {
            h = new();
            h.Quota = new(points);
            await h.Coordinator.RefreshQuotaAsync(Credentials);
            var blocked = await h.Coordinator.RefreshTrafficAsync(Credentials, true);
            var duration = points.Remaining24Hours == 0 ? TimeSpan.FromHours(24) : TimeSpan.FromMinutes(15);
            check(h.TrafficCalls == 0 && h.QuotaCalls == 1 && blocked.NextManualAt == h.Now.Add(duration)
                && !blocked.Cached && !string.IsNullOrEmpty(blocked.Status) && !string.IsNullOrEmpty(blocked.QuotaStatus), "known exhausted API points pause traffic " + points);
            check(blocked.NextAutomaticAt == h.Now.Add(duration > TimeSpan.FromHours(8) ? duration : TimeSpan.FromHours(8)), "known quota does not shorten eight-hour automatic interval");
            h.Now = h.Now.Add(duration);
            await h.Coordinator.RefreshTrafficAsync(Credentials, true);
            check(h.TrafficCalls == 1 && h.QuotaCalls == 1, "traffic resumes after conservative hold without automatic API quota polling");
        }
        h = new(); h.Quota = new(new(0, 0));
        await h.Coordinator.RefreshQuotaAsync(Credentials);
        await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        h.Now = h.Now.AddMinutes(1); h.Quota = new(new(5, 5));
        var recovered = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(recovered.NextManualAt == h.Now && recovered.NextAutomaticAt == h.Now.AddHours(8) && recovered.Status == "" && recovered.QuotaStatus == "", "manual positive quota releases exhausted hold");
        await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        check(h.TrafficCalls == 1, "traffic manually resumes after quota recovery");

        h = new(); h.Quota = new(new(0, 1));
        await h.Coordinator.RefreshQuotaAsync(Credentials);
        var shortHold = await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        h.Now = h.Now.AddMinutes(15); h.Quota = new(new(1, 1));
        var expiredHold = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(expiredHold.NextAutomaticAt == shortHold.NextAutomaticAt, "quota recovery at expired hold boundary leaves automatic schedule unchanged");
        h = new(); h.Quota = new(new(1, 0));
        await h.Coordinator.RefreshQuotaAsync(Credentials);
        var longHold = await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        h.Now = h.Now.AddMinutes(1); h.Quota = new(null, "限流", 1800);
        var limitedDuringHold = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(limitedDuringHold.NextManualAt == longHold.NextManualAt && limitedDuringHold.NextAutomaticAt == longHold.NextAutomaticAt,
            "short HTTP retry cannot shorten existing exhausted daily hold");

        h = new(); await h.Coordinator.RefreshTrafficAsync(Credentials); await h.Coordinator.RefreshQuotaAsync(Credentials);
        var oldTraffic = h.Traffic; var oldTime = h.Now;
        h.Now = h.Now.AddMinutes(1); h.Quota = new(null, "配额失败");
        var quotaFailed = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(quotaFailed.Traffic == oldTraffic && quotaFailed.TrafficUpdatedAt == oldTime && quotaFailed.QuotaCheckedAt == oldTime
            && quotaFailed.Quota == new BandwagonApiQuota(100, 1000) && quotaFailed.QuotaStatus == "配额失败", "quota failure retains traffic and last quota with original timestamps");
        check(quotaFailed.NextQuotaAt == h.Now.AddMinutes(15), "failed quota fifteen-minute cooldown");
        await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(h.QuotaCalls == 2, "failed quota retries blocked");
        h.Traffic = new TrafficData { Error = "流量失败" };
        var trafficFailed = await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        check(trafficFailed.Traffic == oldTraffic && trafficFailed.TrafficUpdatedAt == oldTime && trafficFailed.Status == "流量失败"
                && !trafficFailed.Cached && trafficFailed.NextManualAt == h.Now.AddMinutes(15) && trafficFailed.NextAutomaticAt == h.Now.AddHours(8), "traffic failure retains last success and eight-hour automatic schedule");

        foreach (bool quotaLimited in new[] { false, true })
        {
            h = new();
            if (quotaLimited) { h.Quota = new(null, "限流", 1800); await h.Coordinator.RefreshQuotaAsync(Credentials); }
            else { h.Traffic = new TrafficData { Error = "限流", RetryAfterSeconds = 1800 }; await h.Coordinator.RefreshTrafficAsync(Credentials); }
            var hold = await h.Coordinator.RefreshTrafficAsync(Credentials, true);
            await h.Coordinator.RefreshQuotaAsync(Credentials);
            check(h.TrafficCalls + h.QuotaCalls == 1 && hold.NextManualAt == h.Now.AddMinutes(30) && hold.NextQuotaAt == h.Now.AddMinutes(30)
                && hold.NextAutomaticAt == h.Now.AddHours(8), "HTTP limit blocks both manual endpoints " + quotaLimited);
        }
        h = new(); h.Traffic = new TrafficData { RetryAfterSeconds = long.MaxValue };
        var capped = await h.Coordinator.RefreshTrafficAsync(Credentials);
        check(capped.Status.Length > 0 && capped.NextManualAt == h.Now.AddDays(1) && capped.NextAutomaticAt == h.Now.AddDays(1), "retry cap one day and missing error fallback");
        h = new(); h.Quota = new(null, null, long.MaxValue);
        var quotaCapped = await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(quotaCapped.QuotaStatus.Length > 0 && quotaCapped.NextQuotaAt == h.Now.AddDays(1) && quotaCapped.NextAutomaticAt == h.Now.AddDays(1), "quota retry cap and fallback status");

        h = new(); await h.Coordinator.RefreshTrafficAsync(Credentials);
        await h.Coordinator.RefreshTrafficAsync(new("123", "new-key"));
        await h.Coordinator.RefreshTrafficAsync(new("124", "fixture-key"));
        check(h.TrafficCalls == 3 && h.QuotaCalls == 0, "account or API key change has independent cache");
        check((await h.Coordinator.RefreshTrafficAsync(new(" 123 ", " fixture-key "))).Cached && h.TrafficCalls == 3, "normalized saved credentials cannot bypass cooldown");
        await h.Coordinator.RefreshTrafficAsync(new("12", "3"));
        await h.Coordinator.RefreshTrafficAsync(new("1", "23"));
        check(h.TrafficCalls == 5, "credential cache key has unambiguous field boundary");

        h = new(); var source = new TaskCompletionSource<TrafficData>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.PendingTraffic = source.Task;
        var running = h.Coordinator.RefreshTrafficAsync(Credentials, true);
        var queued = h.Coordinator.RefreshTrafficAsync(Credentials, true);
        using (var cancelQueued = new CancellationTokenSource())
        {
            var cancelled = h.Coordinator.RefreshQuotaAsync(Credentials, cancelQueued.Token);
            cancelQueued.Cancel();
            try { await cancelled; throw new Exception("Expected queued cancellation"); }
            catch (OperationCanceledException) { check(true, "queued quota cancellation does not release owned gate"); }
        }
        source.SetResult(ValidTraffic()); await running;
        check((await queued).Cached && h.TrafficCalls == 1, "concurrent manual refreshes make one request");
        h.PendingTraffic = null;
        await h.Coordinator.RefreshQuotaAsync(Credentials);
        check(h.QuotaCalls == 1, "gate remains usable after queued cancellation");

        h = new(); h.PendingTraffic = new TaskCompletionSource<TrafficData>().Task;
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = h.Coordinator.RefreshTrafficAsync(Credentials, true, cancel.Token);
            cancel.Cancel();
            try { await cancelled; throw new Exception("Expected active cancellation"); }
            catch (OperationCanceledException) { check(true, "active request cancellation propagates"); }
        }
        check((await h.Coordinator.RefreshTrafficAsync(Credentials, true)).Cached && h.TrafficCalls == 1, "cancelled traffic preserves cooldown");
        h.PendingTraffic = null; h.Now = h.Now.AddMinutes(15);
        await h.Coordinator.RefreshTrafficAsync(Credentials, true);
        check(h.TrafficCalls == 2, "gate recovers after active cancellation");
    }
}
