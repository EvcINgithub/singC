using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using singC.Helpers;
using singC.Models;

internal static class BandwagonTrafficTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); } catch (T ex) { check(!string.IsNullOrEmpty(ex.Message), name); return; }
            throw new Exception("Expected " + name);
        }
        const string key = "fixture-private-key&+?=测试";
        var credentials = BandwagonCredentials.Create(" 12345 ", " " + key + " ");
        check(credentials.Veid == "12345" && credentials.ApiKey == key, "quota trims credentials");
        check(!credentials.ToString().Contains(key) && !credentials.ToString().Contains("12345"), "quota ToString redacts credentials");
        foreach (string? veid in new[] { null, "", " ", "0", "000", "-1", "1a", "１", new string('1', 33) })
            Reject<ArgumentException>(() => BandwagonCredentials.Create(veid, key), "quota invalid VEID");
        foreach (string? secret in new[] { null, "", " ", "a\nb", new string('k', 4097) })
            Reject<ArgumentException>(() => BandwagonCredentials.Create("1", secret), "quota invalid key");
        check(BandwagonCredentials.Create(new string('1', 32), new string('k', 4096)).ApiKey.Length == 4096, "quota length boundaries");
        check(BandwagonCredentials.Create("109", key).Veid == "109", "quota accepts zero and nine within valid positive VEID");
        Reject<ArgumentException>(() => BandwagonCredentials.Create("1", null), "quota null key with valid VEID");
        using (var handler = BandwagonTrafficService.CreateHandler())
            check(!handler.AllowAutoRedirect && !handler.UseCookies && handler.UseProxy, "quota production transport blocks redirects and cookies while honoring system proxy");
        check(BandwagonTrafficService.Instance != null, "quota production client available");
        string path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        var settings = new BandwagonSettings(store, WindowsSecretProtection.Protect, WindowsSecretProtection.Unprotect);
        check(settings.Load() == null && settings.Version == null, "quota initially unconfigured");
        settings.Save("12345", key);
        check(!File.ReadAllText(path).Contains(key) && !File.ReadAllText(path).Contains("12345"), "quota encrypted on disk");
        check(new BandwagonSettings(new SettingsStore(path), WindowsSecretProtection.Protect, WindowsSecretProtection.Unprotect).Load() == credentials, "quota DPAPI reload");
        check(WindowsSecretProtection.Unprotect(WindowsSecretProtection.Protect("")) == "", "quota empty encryption boundary");
        Reject<FormatException>(() => WindowsSecretProtection.Unprotect("not-base64"), "quota invalid encrypted format");
        Reject<CryptographicException>(() => WindowsSecretProtection.Unprotect(Convert.ToBase64String(new byte[16])), "quota damaged ciphertext");
        var version = settings.Version;
        Reject<ArgumentException>(() => settings.Save("", key), "quota partial save rejected");
        check(settings.Version == version && settings.Load() == credentials, "quota invalid save retains pair");
        var failing = new BandwagonSettings(store, _ => throw new CryptographicException(), WindowsSecretProtection.Unprotect);
        Reject<CryptographicException>(() => failing.Save("2", "other"), "quota encryption failure reported");
        check(settings.Version == version, "quota encryption failure retains pair");
        var blocked = Path.Combine(directory, "blocked.json");
        var blockedStore = new SettingsStore(blocked);
        check(blockedStore.Get(BandwagonSettings.StorageKey) == null, "quota initializes blocked store");
        Directory.CreateDirectory(blocked);
        var blockedSettings = new BandwagonSettings(blockedStore, WindowsSecretProtection.Protect, WindowsSecretProtection.Unprotect);
        Reject<UnauthorizedAccessException>(() => blockedSettings.Save("1", key), "quota disk write failure reported");
        check(blockedSettings.Load() == null, "quota failed write retains memory");
        foreach (string invalid in new[] { "null", "{", "{}", "{\"Veid\":\"1\",\"ApiKey\":null}" })
        {
            store.Set(BandwagonSettings.StorageKey, WindowsSecretProtection.Protect(invalid));
            Reject<IOException>(() => settings.Load(), "quota malformed decrypted record");
        }
        store.Set(BandwagonSettings.StorageKey, "bad");
        Reject<IOException>(() => settings.Load(), "quota malformed ciphertext error");
        check(settings.Version == "bad", "quota preserves damaged record");
        store.Set(BandwagonSettings.StorageKey, Convert.ToBase64String(new byte[16]));
        Reject<IOException>(() => settings.Load(), "quota DPAPI read failure safe");
        settings.Save(" ", " ");
        check(settings.Load() == null && settings.Version == "", "quota clear disables query");
        settings.Save("1", key); settings.Save("", "");
        check(settings.Load() == null, "quota empty fields clear existing credentials");
        store.Set("TrafficServiceUrl", "https://old.example/private");
        check(settings.Load() == null, "quota legacy URL ignored");

        const string valid = "{\"error\":0,\"data_counter\":1073741824,\"plan_monthly_data\":4294967296,\"monthly_data_multiplier\":2,\"data_next_reset\":1700000000}";
        async Task<TrafficData> Query(string json, HttpStatusCode status = HttpStatusCode.OK, RetryConditionHeaderValue? retry = null)
        {
            using var client = new HttpClient(new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(json) };
                response.Headers.RetryAfter = retry;
                return Task.FromResult(response);
            }));
            return await new BandwagonTrafficService(client).QueryAsync(credentials);
        }
        using (var client = new HttpClient(new Handler(async (request, token) =>
        {
            check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://api.64clouds.com/v1/getServiceInfo", "quota fixed HTTPS POST endpoint without URL secrets");
            using var expected = new FormUrlEncodedContent(new Dictionary<string,string>{{"veid","12345"},{"api_key",key}});
            check(await request.Content!.ReadAsStringAsync(token) == await expected.ReadAsStringAsync(token), "quota form encoded credentials");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(valid) };
        })))
        {
            var data = await new BandwagonTrafficService(client).QueryAsync(credentials);
            check(data.HasTrafficData && data.UsedGB == 2 && data.RemainingGB == 6 && data.ResetTimestamp == 1700000000, "quota multiplier applies to allowance and usage");
        }
        var over = await Query(valid.Replace("1073741824", "5368709120"));
        check(over.HasTrafficData && over.UsedGB == 10 && over.RemainingGB == 0, "quota remaining clamps zero");
        var zero = await Query(valid.Replace("1073741824", "0").Replace("4294967296", "0").Replace("1700000000", "0"));
        check(zero.HasTrafficData && zero.UsedGB == 0 && zero.RemainingGB == 0 && zero.ResetTimestamp == 0, "quota zero counters");
        var fraction = await Query(valid.Replace(":2,", ":0.5,"));
        check(fraction.UsedGB == 0.5 && fraction.RemainingGB == 1.5, "quota fractional multiplier");
        var quoted = await Query("{\"error\":\"0\",\"data_counter\":\"1073741824\",\"plan_monthly_data\":\"4294967296\",\"monthly_data_multiplier\":\"0.5\",\"data_next_reset\":\"1700000000\"}");
        check(quoted.HasTrafficData && quoted.UsedGB == 0.5 && quoted.RemainingGB == 1.5 && quoted.ResetTimestamp == 1700000000, "quota accepts numeric strings invariantly");
        foreach (var change in new[] {
            ("1073741824", "-1"), ("4294967296", "-1"), (":2,", ":0,"), (":2,", ":-1,"),
            ("1073741824", "1e309"), ("4294967296", "1e309"), (":2,", ":1e309,"),
            ("1073741824", "1e308"), ("4294967296", "1e308"),
            ("1700000000", "-1"), ("1700000000", "253402300800"), ("1700000000", "1.1") })
        {
            var result = await Query(valid.Replace(change.Item1, change.Item2));
            check(!result.HasTrafficData && !string.IsNullOrEmpty(result.Error), "quota invalid numeric data " + change);
        }
        check((await Query(valid.Replace("1700000000", "253402300799"))).HasTrafficData, "quota maximum timestamp");
        foreach (var json in new[] { "null", "[]", "{}", "{", "{\"error\":\"0\"}", valid.Replace("\"data_counter\":1073741824,", ""), valid.Replace("1073741824", "null"), valid.Replace("1700000000", "\"invalid\""), valid.Replace("1073741824", "\"invalid\""), valid.Replace("1700000000", "\"999999999999999999999999\"") })
        {
            var result = await Query(json);
            check(!result.HasTrafficData && !string.IsNullOrEmpty(result.Error), "quota malformed response safe");
        }
        var apiError = await Query("{\"error\":-1,\"message\":\"fixture-private-key\"}");
        check(apiError.Error!.Contains("拒绝") && !apiError.Error.Contains("fixture-private-key"), "quota API errors omit server message");
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError, HttpStatusCode.Redirect })
        {
            var result = await Query(valid, status);
            check(!result.HasTrafficData && !string.IsNullOrEmpty(result.Error), "quota HTTP error or redirect " + status);
        }
        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests })
        {
            var result = await Query("secret", status);
            check(result.RetryAfterSeconds == 900 && !string.IsNullOrEmpty(result.Error), "quota default throttle delay");
        }
        foreach (var item in new[] { (Seconds:1,Expected:60L), (Seconds:120,Expected:120L), (Seconds:100000,Expected:86400L) })
            check((await Query("", HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromSeconds(item.Seconds)))).RetryAfterSeconds == item.Expected, "quota Retry-After bound " + item.Seconds);
        var dated = await Query("", HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1)));
        check(dated.RetryAfterSeconds is >= 3590 and <= 3600, "quota Retry-After date");
        check((await Query(new string('x', 1024 * 1024 + 1))).Error != null, "quota one MiB response limit");
        foreach (Exception error in new Exception[] { new HttpRequestException(key), new IOException(key), new TaskCanceledException(key) })
        {
            using var client = new HttpClient(new Handler((_, _) => throw error));
            var result = await new BandwagonTrafficService(client).QueryAsync(credentials);
            check(!string.IsNullOrEmpty(result.Error) && !result.Error.Contains(key), "quota transport errors redact secrets");
        }
        using (var client = new HttpClient(new Handler((_, token) => { token.ThrowIfCancellationRequested(); throw new Exception("Expected cancellation"); })))
        {
            try { await new BandwagonTrafficService(client).QueryAsync(credentials, new CancellationToken(true)); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { check(true, "quota caller cancellation"); }
        }
    }
}
