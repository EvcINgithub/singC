using System.Text.Json;
using singC.Helpers;

internal static class ProxyDiagnosticLogTests
{
    private sealed class Backend : ISystemProxyBackend
    {
        public SystemProxySettings Current = new(1, "user:secret@proxy.test:8080", "private.internal", "https://pac.test/path?token=SECRET");
        public bool FailWrite;
        public SystemProxySettings Read() => Current;
        public void Write(SystemProxySettings settings)
        {
            if (FailWrite) throw new IOException("SECRET exception content");
            Current = settings;
        }
    }

    public static void Run(string root, Action<bool, string> check)
    {
        string journal = Path.Combine(root, "lease.json");
        string log = Path.Combine(root, "proxy-diagnostics.jsonl");
        var backend = new Backend();
        var original = backend.Current;
        var lease = new SystemProxyLease(backend, journal, () => true);
        lease.Apply("127.0.0.1", 7890);
        var applied = backend.Current;
        lease.Restore();
        var records = Read(log);
        check(backend.Current == original && records.Any(r => Stage(r) == "restore.write-succeeded"), "proxy log: normal restoration remains unchanged and is recorded");
        check(records.All(r => r.GetProperty("Session").GetString() == records[0].GetProperty("Session").GetString()), "proxy log: one lease has a stable correlation ID");
        check(records[0].GetProperty("Schema").GetInt32() == 1
            && records[0].GetProperty("ProcessId").GetInt32() == Environment.ProcessId
            && records[0].GetProperty("TimestampUtc").GetDateTime().Kind == DateTimeKind.Utc,
            "proxy log: schema, process and UTC timestamp are available for correlation");
        var saved = records.Single(r => Stage(r) == "apply.journal-saved").GetProperty("Original").GetProperty("Server");
        check(saved.GetProperty("Length").GetInt32() == original.Server.Length
            && saved.GetProperty("Sha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original.Server)))
            && !saved.GetProperty("Empty").GetBoolean(), "proxy log: field fingerprints reflect the actual captured values");
        lease.Trace("test.native-error", new System.ComponentModel.Win32Exception(5));
        var native = Read(log).Last();
        check(native.GetProperty("NativeError").GetInt32() == 5
            && native.GetProperty("HResult").GetInt32() == new System.ComponentModel.Win32Exception(5).HResult,
            "proxy log: native failure codes are preserved without exception text");
        check(!File.ReadAllText(log).Contains("SECRET") && !File.ReadAllText(log).Contains("private.internal")
            && !File.ReadAllText(log).Contains("user:secret"), "proxy log: PAC, bypass, proxy credentials and exception content are not disclosed");
        var cases = new[] {
            (Name: "Flags", Settings: applied with { Flags = 1 }),
            (Name: "Server", Settings: applied with { Server = "other:9000" }),
            (Name: "Bypass", Settings: applied with { Bypass = "new.private" }),
            (Name: "AutoConfigUrl", Settings: applied with { AutoConfigUrl = "https://new.test/private" })
        };
        foreach (var item in cases)
        {
            backend.Current = original;
            lease.Apply("127.0.0.1", 7890);
            backend.Current = item.Settings;
            lease.Restore();
            var compare = Read(log).Last(r => Stage(r) == "restore.compare");
            check(compare.GetProperty("ChangedFields").EnumerateArray().Select(f => f.GetString()).SequenceEqual(new[] { item.Name }), "proxy log: identifies changed " + item.Name);
            check(backend.Current == original && new FileInfo(journal).Length == 0
                && Read(log).Any(r => Stage(r) == "restore.forced-original"), "proxy log: startup snapshot is forced despite changed " + item.Name);
        }
        backend.Current = original;
        lease.Apply("127.0.0.1", 7890);
        backend.FailWrite = true;
        try { lease.Restore(); throw new Exception("Expected restoration failure"); }
        catch (IOException)
        {
            var failure = Read(log).Last(r => Stage(r) == "restore.failed");
            check(failure.GetProperty("ErrorType").GetString() == typeof(IOException).FullName
                && new FileInfo(journal).Length > 0, "proxy log: failure type recorded and failed journal retained");
            check(!File.ReadAllText(log).Contains("SECRET"), "proxy log: failure messages cannot leak secrets");
        }
        backend.FailWrite = false;
        lease.Restore();
        lease.Trace("test.repeated-stop");
        foreach (string line in File.ReadLines(log)) { using var parsed = JsonDocument.Parse(line); }
        check(true, "proxy log: every record is valid JSON after repeated operations");

        File.WriteAllText(log, new string('x', (int)ProxyDiagnosticLog.MaximumBytes));
        lease.Trace("test.rotation");
        check(File.Exists(log + ".previous") && Read(log).Single().GetProperty("Stage").GetString() == "test.rotation", "proxy log: bounded rotation retains previous log");

        var blockedRoot = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blockedRoot);
        Directory.CreateDirectory(Path.Combine(blockedRoot, "proxy-diagnostics.jsonl"));
        var blocked = new SystemProxyLease(backend, Path.Combine(blockedRoot, "lease.json"), () => true);
        backend.Current = original;
        blocked.Apply("127.0.0.1", 7890);
        blocked.Restore();
        check(backend.Current == original, "proxy log: unusable log destination cannot prevent restoration");

        File.WriteAllText(journal, "{invalid");
        try { lease.Recover(); throw new Exception("Expected malformed journal failure"); }
        catch (JsonException) { check(Read(log).Any(r => Stage(r) == "recover.failed"), "proxy log: invalid journal recovery failure is recorded"); }
    }

    private static string? Stage(JsonElement record) => record.GetProperty("Stage").GetString();
    private static JsonElement[] Read(string path) => File.ReadLines(path).Select(line =>
    { using var doc = JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray();
}
