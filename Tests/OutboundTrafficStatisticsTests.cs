using System.Text;
using System.Text.Json;
using singC.Models;

internal static class OutboundTrafficStatisticsTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var time = new DateTimeOffset(2026, 9, 30, 23, 59, 55, TimeSpan.FromHours(8));
        string path = Path.Combine(directory, "outbounds.json");
        using var stats = new OutboundTrafficStatistics(path);
        ConnectionTrafficSample Sample(string id, string? tag, long up, long down, int start = -10)
            => new(id, time.AddSeconds(start), tag, up, down);
        OutboundTrafficSnapshot Snapshot(OutboundTrafficPeriod period = OutboundTrafficPeriod.Session, int second = 0)
            => stats.GetSnapshot(period, time.AddSeconds(second));
        TrafficTotals Totals(string tag, OutboundTrafficPeriod period = OutboundTrafficPeriod.Session, int second = 0)
            => Snapshot(period, second).Rows.SingleOrDefault(r => r.Tag == tag)?.Totals ?? new();
        check(Snapshot().Rows.Count == 0, "outbound: empty history");
        stats.Record([Sample("a", "direct", 100, 200), Sample("b", "proxy", 200, 300)], time);
        check(Snapshot().Rows.Count == 0, "outbound: first frame only establishes baselines");
        stats.Record([Sample("a", "direct", 150, 270), Sample("b", "proxy", 300, 500)], time.AddSeconds(1));
        check(Totals("direct") == new TrafficTotals(50, 70) && Totals("proxy") == new TrafficTotals(100, 200)
            && Snapshot().Rows[0].Tag == "proxy", "outbound: per-tag deltas sorted by combined traffic");
        stats.Record([Sample("a", "direct", 150, 270), Sample("a", "direct", 150, 270), Sample("b", "proxy", 300, 500)], time.AddSeconds(2));
        check(Totals("direct") == new TrafficTotals(50, 70), "outbound: repeated rows and snapshots are not counted twice");
        stats.Record([Sample("a", "direct", 180, 300), Sample("b", "proxy", 320, 520),
            Sample("c", "proxy", 70, 90, 3), Sample("old", "old", 999, 999),
            Sample("unknown", null, 5, 7, 3)], time.AddSeconds(3));
        check(Totals("proxy") == new TrafficTotals(190, 310) && Totals("old") == new TrafficTotals(),
            "outbound: new connections count first observed bytes, older first sightings are baselined");
        check(Totals("") == new TrafficTotals(5, 7) && Snapshot().Rows.Single(r => r.Tag == "").Name == "未知出站",
            "outbound: missing outbound is classified as unknown");
        stats.Record([Sample("a", "direct", 1, 2)], time.AddSeconds(4));
        check(Totals("direct") == new TrafficTotals(80, 100), "outbound: rollback resets both counters without recounting");
        stats.Record([Sample("a", "direct", 11, 22)], time.AddSeconds(6));
        check(Totals("direct", OutboundTrafficPeriod.Today, 6) == new TrafficTotals(10, 20)
            && Totals("direct", OutboundTrafficPeriod.Month, 6) == new TrafficTotals(10, 20)
            && Totals("direct", OutboundTrafficPeriod.Today) == new TrafficTotals(80, 100),
            "outbound: deltas use observation date across midnight and month boundaries");
        stats.Record([], time.AddSeconds(7));
        check(Totals("direct") == new TrafficTotals(90, 120), "outbound: closed connections retain totals without guessing tail bytes");
        stats.Record([Sample("a", "direct", 999, 999)], time.AddSeconds(8));
        check(Totals("direct") == new TrafficTotals(90, 120), "outbound: reappearing older connections are not recounted");
        stats.Record([Sample("a", "direct", 1009, 1009)], time.AddSeconds(20));
        check(Totals("direct") == new TrafficTotals(100, 130), "outbound: known connection baseline survives a reconnect gap");
        stats.Record([Sample("a", "proxy", 4, 6, 21)], time.AddSeconds(21));
        check(Totals("proxy") == new TrafficTotals(194, 316), "outbound: reused ID with new start time identifies a new connection");
        stats.Save(force: true);
        using var restored = new OutboundTrafficStatistics(path);
        check(restored.GetSnapshot(OutboundTrafficPeriod.AllTime, time).Rows.SequenceEqual(Snapshot().Rows)
            && restored.GetSnapshot(OutboundTrafficPeriod.Session, time).Rows.Count == 0,
            "outbound: persisted daily totals restore independently of session");
        restored.Record([Sample("a", "proxy", 500, 600, 21)], time.AddSeconds(22));
        check(restored.GetSnapshot(OutboundTrafficPeriod.Session, time).Rows.Count == 0,
            "outbound: app restart cannot recount existing connection bytes");
        stats.BeginSession();
        check(Snapshot().Rows.Count == 0 && Snapshot(OutboundTrafficPeriod.AllTime).Rows.Count == 3,
            "outbound: mode/core restart resets session but preserves history");
        stats.Record([], time.AddSeconds(30));
        stats.Record([Sample("fresh", "proxy", 10, 20, 31)], time.AddSeconds(31));
        check(Totals("proxy", OutboundTrafficPeriod.AllTime) == new TrafficTotals(204, 336),
            "outbound: same tag merges across configuration/session changes");
        stats.Record([Sample("fresh", "proxy", 10, 20, 31), Sample("rename", "renamed", 1, 1, 32)], time.AddSeconds(32));
        check(Totals("renamed") == new TrafficTotals(1, 1), "outbound: renamed tag creates a separate bucket");

        using var invalid = new OutboundTrafficStatistics(Path.Combine(directory, "invalid-samples.json"));
        invalid.Record([], time);
        invalid.Record([Sample("", "proxy", 1, 1, 1), Sample("negative", "proxy", -1, 1, 1),
            Sample("future", "proxy", 1, 1, 50), new("invalid-time", default, "proxy", 1, 1)], time.AddSeconds(1));
        check(invalid.GetSnapshot(OutboundTrafficPeriod.AllTime, time).Rows.Count == 0, "outbound: invalid samples ignored");
        invalid.Record([Sample("new", "proxy", 10, 10, 2)], time.AddSeconds(2));
        invalid.Record([Sample("new", "proxy", 9, 100, 2)], time.AddSeconds(3));
        check(invalid.GetSnapshot(OutboundTrafficPeriod.Session, time).Rows[0].Totals == new TrafficTotals(10, 10),
            "outbound: partial counter rollback also rebaselines both directions");

        foreach (string bad in new[] { "not json", "null", "{\"bad-date\":{}}", "{\"2026-09-30\":null}",
            "{\"2026-09-30\":{\"proxy\":null}}", "{\"2026-09-30\":{\"proxy\":{\"UploadBytes\":-1}}}" })
        {
            string damagedPath = Path.Combine(directory, "damaged.json");
            File.WriteAllText(damagedPath, bad);
            using var damaged = new OutboundTrafficStatistics(damagedPath);
            damaged.Record([], time);
            damaged.Record([Sample("a", "proxy", 1, 2, 1)], time.AddSeconds(1));
            damaged.Save(force: true);
            check(damaged.GetSnapshot(OutboundTrafficPeriod.Today, time).StorageError.Length > 0
                && File.ReadAllText(damagedPath) == bad, "outbound: unreadable history is preserved");
        }
        string blockedPath = Path.Combine(directory, "blocked");
        using var blocked = new OutboundTrafficStatistics(blockedPath);
        blocked.Record([], time);
        blocked.Record([Sample("a", "proxy", 1, 2, 1)], time.AddSeconds(1));
        Directory.CreateDirectory(blockedPath);
        blocked.Save(force: true);
        check(blocked.GetSnapshot(OutboundTrafficPeriod.Today, time).StorageError.Length > 0,
            "outbound: failed write reports an error");
        Directory.Delete(blockedPath);
        blocked.Save(force: true);
        check(blocked.GetSnapshot(OutboundTrafficPeriod.Today, time).StorageError.Length == 0 && File.Exists(blockedPath),
            "outbound: failed write can be retried");

        using var service = new ClashWebSocketService();
        IReadOnlyList<ConnectionTrafficSample> received = [];
        int events = 0;
        service.OnConnectionTrafficReceived += samples => { received = samples; events++; };
        void Message(string json) => service.ProcessMessage(Encoding.UTF8.GetBytes(json));
        string Connection(string id, string chains) => $$"""
            {"id":"{{id}}","start":"2026-09-30T15:59:55Z","upload":3000000000,"download":4000000000,"chains":{{chains}}}
            """;
        Message("{\"connections\":[" + Connection("a", "[\"node-A\",\"nested\",\"proxy\"]") + ","
            + Connection("b", "[\"direct\"]") + "," + Connection("c", "[]") + "]}");
        check(received.Count == 3 && received[0].Outbound == "proxy" && received[1].Outbound == "direct"
            && received[2].Outbound == null && received[0].UploadBytes == 3000000000,
            "outbound: wire parser selects routing tag once and retains 64-bit counters");
        Message("{\"connections\":[null,{}," + Connection("x", "[\"node\",42]") + "]}");
        check(received.Count == 1 && received[0].Outbound == null, "outbound: malformed rows skipped and invalid chain is unknown");
        int before = events;
        Message("{}");
        Message("{\"connections\":42}");
        check(events == before, "outbound: missing or invalid snapshot does not clear baselines");
        Message("{\"connections\":null}");
        check(received.Count == 0, "outbound: null connections is an empty snapshot");

        List<ConnectionInfo>? displayed = null;
        service.OnConnectionsReceived += connections => displayed = connections;
        var complete = JsonSerializer.Serialize(new { connections = new[] { new {
            id = "complete", start = time, upload = 10, download = 20, chains = new[] { "node", "proxy" }, rule = "final",
            metadata = new { network = "tcp", sourceIP = "127.0.0.1", sourcePort = "1", destinationIP = "127.0.0.1", destinationPort = "2" }
        } } });
        Message(complete);
        check(displayed?.Single().OutboundTag == "proxy" && received.Single().Outbound == "proxy",
            "outbound: connection UI and independent statistics use the same routing tag");
    }
}
