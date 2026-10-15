using System.Text.Json;
using singC.Helpers;

internal static class ForcedProxyRestoreTests
{
    private sealed class Backend : ISystemProxyBackend
    {
        public SystemProxySettings Current = new(1, "", "", "");
        public bool FailRead;
        public bool FailWrite;
        public bool PartialWrite;
        public int FailWritesRemaining;
        public int Writes;
        public SystemProxySettings Read() => FailRead ? throw new IOException("Simulated read failure") : Current;
        public void Write(SystemProxySettings settings)
        {
            Writes++;
            bool transientFailure = FailWritesRemaining > 0;
            if (transientFailure) FailWritesRemaining--;
            if (FailWrite || transientFailure)
            {
                if (PartialWrite) Current = settings;
                throw new IOException("Simulated write failure");
            }
            Current = settings;
        }
    }

    public static void Run(string root, Action<bool, string> check)
    {
        string path = Path.Combine(root, "lease.json");
        var backend = new Backend();
        var lease = new SystemProxyLease(backend, path, () => true);
        string logPath = Path.Combine(root, "proxy-diagnostics.jsonl");
        void ExpectStages(int first, string message, params string[] required)
        {
            var stages = File.ReadLines(logPath).Skip(first).Select(line =>
            { using var data = JsonDocument.Parse(line); return data.RootElement.GetProperty("Stage").GetString(); }).ToList();
            int cursor = 0;
            bool found = true;
            foreach (var stage in required)
            {
                int next = stages.FindIndex(cursor, item => item == stage);
                if (next < 0) { found = false; break; }
                cursor = next + 1;
            }
            check(found, message);
        }
        int LogPosition() => File.Exists(logPath) ? File.ReadLines(logPath).Count() : 0;
        var snapshots = new[] {
            new SystemProxySettings(1, "", "", ""),
            new SystemProxySettings(3, "http=127.0.0.1:7899;https=127.0.0.1:7899", "original-bypass", ""),
            new SystemProxySettings(13, "stored-proxy:8080", "company.local;<local>", "https://pac.test/original.pac")
        };
        foreach (var original in snapshots)
        {
            foreach (string change in new[] { "none", "flags", "server", "bypass", "pac", "all" })
            {
                backend.Current = original;
                lease.Apply("127.0.0.1", 7899);
                var applied = backend.Current;
                backend.Current = change switch {
                    "flags" => applied with { Flags = 9 },
                    "server" => applied with { Server = "external:9000" },
                    "bypass" => applied with { Bypass = "external-bypass" },
                    "pac" => applied with { AutoConfigUrl = "https://pac.test/new.pac" },
                    "all" => new SystemProxySettings(13, "external:9000", "external-bypass", "https://pac.test/new.pac"),
                    _ => applied
                };
                int logStart = LogPosition();
                lease.Restore();
                check(backend.Current == original && new FileInfo(path).Length == 0,
                    $"forced restore: snapshot flags {original.Flags}, current change {change}");
                ExpectStages(logStart, $"forced restore: auditable write-and-clear sequence, flags={original.Flags}, change={change}",
                    "restore.requested", "restore.compare", "restore.forced-original", "restore.write-original",
                    "restore.write-succeeded", "restore.journal-cleared", "restore.completed");
            }
        }

        var baseline = snapshots[0];
        backend.Current = baseline;
        lease.Apply("127.0.0.1", 7899);
        backend.FailRead = true;
        int unreadableLog = LogPosition();
        lease.Restore();
        check(backend.Current == baseline && new FileInfo(path).Length == 0,
            "forced restore: unreadable current state cannot block restoring the saved snapshot");
        ExpectStages(unreadableLog, "forced restore: unavailable diagnostics do not conceal successful restoration",
            "restore.read-current-failed", "restore.forced-original", "restore.write-succeeded", "restore.completed");
        backend.FailRead = false;

        foreach (bool partial in new[] { false, true })
        {
            backend.Current = baseline;
            lease.Apply("127.0.0.1", 7899);
            backend.Current = new(15, "external:9000", "changed", "https://pac.test/changed.pac");
            backend.FailWrite = true;
            backend.PartialWrite = partial;
            try { lease.Restore(); throw new Exception("Expected failed restore"); }
            catch (IOException) { check(new FileInfo(path).Length > 0, $"forced restore: failed write retains journal, partial={partial}"); }
            backend.FailWrite = false;
            backend.PartialWrite = false;
            lease.Restore();
            check(backend.Current == baseline && new FileInfo(path).Length == 0,
                $"forced restore: retry completes from retained record, partial={partial}");
        }

        backend.Current = new(3, "external:9000", "external", "");
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            Original = baseline,
            Applied = new SystemProxySettings(3, "http=127.0.0.1:7899;https=127.0.0.1:7899", "applied", "")
        }));
        lease.Recover();
        check(backend.Current == baseline, "forced restore: abandoned legacy system-proxy record follows the same forced policy");
        int writes = backend.Writes;
        lease.Restore();
        check(backend.Writes == writes, "forced restore: completed restoration is idempotent");
        ExpectStages(0, "forced restore: empty-record outcome is visible in diagnostics", "restore.empty-journal");

        backend.Current = snapshots[2];
        lease.Apply(null, null);
        var external = backend.Current with { Server = "external-tun:9000" };
        backend.Current = external;
        int tunLog = LogPosition();
        lease.Restore();
        check(backend.Current == external, "forced restore: external changes during TUN retain the existing matching policy");
        ExpectStages(tunLog, "forced restore: TUN conflicts remain distinguishable from forced system-proxy restores",
            "restore.compare", "restore.skipped-settings-changed", "restore.journal-cleared");

        backend.Current = baseline;
        lease.Apply(null, null);
        backend.FailRead = true;
        try { lease.Restore(); throw new Exception("Expected TUN read failure"); }
        catch (IOException) { check(new FileInfo(path).Length > 0, "forced restore: TUN read failures still preserve the record and propagate"); }
        backend.FailRead = false;
        lease.Restore();

        backend.Current = baseline;
        lease.Apply("127.0.0.1", 7899);
        try { lease.Apply("127.0.0.1", 7900); throw new Exception("Expected duplicate ownership rejection"); }
        catch (InvalidOperationException) { check(backend.Current.Server.Contains(":7899"), "forced restore: duplicate apply cannot replace an active snapshot"); }
        lease.Restore();

        backend.Current = baseline;
        backend.FailWrite = true;
        int rollbackLog = LogPosition();
        try { lease.Apply("127.0.0.1", 7899); throw new Exception("Expected apply and rollback failure"); }
        catch (AggregateException ex)
        {
            check(ex.InnerExceptions.Count == 2 && new FileInfo(path).Length > 0,
                "forced restore: failed apply and rollback preserve both errors and recovery record");
        }
        backend.FailWrite = false;
        ExpectStages(rollbackLog, "forced restore: apply and rollback failures are separately diagnosable",
            "apply.requested", "apply.journal-saved", "apply.write-failed", "apply.rollback-failed", "apply.failed");
        lease.Recover();
        check(backend.Current == baseline && new FileInfo(path).Length == 0,
            "forced restore: recovery remains possible after apply and rollback both fail");

        backend.FailRead = true;
        try { lease.Apply("127.0.0.1", 7899); throw new Exception("Expected startup snapshot read failure"); }
        catch (IOException) { check(backend.Current == baseline, "forced restore: cannot activate a proxy without a readable startup snapshot"); }
        backend.FailRead = false;

        backend.Current = baseline;
        backend.FailWritesRemaining = 1;
        backend.PartialWrite = true;
        int partialApplyLog = LogPosition();
        try { lease.Apply("127.0.0.1", 7899); throw new Exception("Expected partially failed apply"); }
        catch (IOException)
        {
            check(backend.Current == baseline && new FileInfo(path).Length == 0,
                "forced restore: failed activation rolls back the partial write before discarding the backup");
        }
        backend.PartialWrite = false;
        ExpectStages(partialApplyLog, "forced restore: successful apply rollback is visible in diagnostics",
            "apply.write-failed", "apply.rollback-completed", "apply.failed");

        foreach (bool missingOriginal in new[] { true, false })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                Original = missingOriginal ? null : baseline,
                Applied = missingOriginal ? new SystemProxySettings(3, "proxy:7899", "", "") : null
            }));
            int before = backend.Writes;
            try { lease.Recover(); throw new Exception("Expected invalid record rejection"); }
            catch (InvalidDataException ex)
            {
                check(backend.Writes == before && new FileInfo(path).Length > 0 && ex.Message.Contains("恢复记录不完整"),
                    "forced restore: incomplete recovery record is retained, missingOriginal=" + missingOriginal);
            }
        }
        File.WriteAllText(path, "null");
        try { lease.Recover(); throw new Exception("Expected null record rejection"); }
        catch (InvalidDataException ex)
        { check(ex.Message.Contains("恢复记录无效") && File.ReadAllText(path) == "null", "forced restore: null recovery record has actionable feedback and is retained"); }
        File.WriteAllText(path, "");

        for (int round = 0; round < 20; round++)
        {
            backend.Current = baseline;
            lease.Apply("127.0.0.1", 7899);
            backend.Current = backend.Current with { Bypass = "modified-in-round-" + round };
            lease.Restore();
            check(backend.Current == baseline, "forced restore: proxy stage restores baseline in round " + round);
            lease.Apply(null, null);
            lease.Restore();
            check(backend.Current == baseline, "forced restore: TUN stage does not inherit the previous proxy in round " + round);
        }
    }
}
