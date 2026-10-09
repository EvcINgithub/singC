using System.Text;
using singC.Models;

internal static class TrafficStatisticsTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var time = new DateTimeOffset(2026, 9, 30, 23, 59, 58, TimeSpan.FromHours(8));
        string path = Path.Combine(directory, "statistics.json");
        using var statistics = new TrafficStatistics(path);
        TrafficStatisticsSnapshot Snapshot(double seconds = 2) => statistics.GetSnapshot(time, seconds);
        check(Snapshot().AllTime == new TrafficTotals(), "traffic: empty history");
        statistics.BeginSession();
        statistics.Record(100, 200, time, 0);
        check(Snapshot().Session == new TrafficTotals(100, 200) && Snapshot().UploadSpeed == 0,
            "traffic: first sample includes startup traffic without speed spike");
        statistics.Record(300, 600, time.AddSeconds(1), 1);
        check(Snapshot().Session == new TrafficTotals(300, 600) && Snapshot().UploadSpeed == 200
            && Snapshot().DownloadSpeed == 400, "traffic: totals and elapsed-time speeds");
        statistics.Record(300, 600, time.AddSeconds(2), 2);
        check(Snapshot().AllTime == new TrafficTotals(300, 600) && Snapshot().UploadSpeed == 0,
            "traffic: duplicate sample is not counted twice");
        statistics.MarkDisconnected();
        statistics.Record(400, 800, time.AddSeconds(3), 3);
        check(Snapshot().AllTime == new TrafficTotals(400, 800) && Snapshot().UploadSpeed == 0,
            "traffic: reconnect keeps baseline and suppresses gap speed");
        var nextMonth = statistics.GetSnapshot(time.AddSeconds(3), 3);
        check(nextMonth.Today == new TrafficTotals(100, 200) && nextMonth.Month == nextMonth.Today
            && nextMonth.RecentDays[1].Totals == new TrafficTotals(300, 600),
            "traffic: midnight and month rollover retain prior history");
        statistics.Record(20, 40, time.AddSeconds(4), 4);
        check(Snapshot(4).AllTime == new TrafficTotals(420, 840) && Snapshot(4).UploadSpeed == 0,
            "traffic: core counter reset counts new traffic without negative deltas");
        statistics.Record(-1, 10, time, 5);
        check(Snapshot().AllTime == new TrafficTotals(420, 840), "traffic: negative sample rejected");
        statistics.Record(120, 140, time.AddSeconds(5), 5);
        check(Snapshot(11).UploadSpeed == 0 && Snapshot(11).DownloadSpeed == 0,
            "traffic: stale samples clear both speeds");
        statistics.Save(force: true);
        using var restored = new TrafficStatistics(path);
        check(restored.GetSnapshot(time, 12).AllTime == Snapshot(12).AllTime,
            "traffic: daily history survives application restart");
        check(restored.GetSnapshot(time, 12).Session == new TrafficTotals(),
            "traffic: session is not restored as current usage");
        restored.BeginSession();
        restored.Record(10, 20, time, 13);
        check(restored.GetSnapshot(time, 13).AllTime == new TrafficTotals(530, 960),
            "traffic: new process appends to persisted history");
        statistics.BeginSession();
        statistics.Record(7, 9, time, 12);
        check(Snapshot(12).Session == new TrafficTotals(7, 9)
            && Snapshot(12).AllTime == new TrafficTotals(527, 949),
            "traffic: explicit session restart retains historical totals");
        check(statistics.GetSnapshot(time.AddDays(2), 13).Today == new TrafficTotals(),
            "traffic: day changes while stopped show zero today");
        check(Snapshot(12).RecentDays.Count == 7, "traffic: recent history includes empty days");
        check(TrafficTotals.FormatBytes(1024).Contains("KB")
            && TrafficTotals.FormatBytes(1024d * 1024 * 1024 * 1024).Contains("TB"),
            "traffic: binary units scale to large totals");

        foreach (string bad in new[] { "not json", "null", "{\"2026-09-30\":null}",
                     "{\"invalid-date\":{\"UploadBytes\":1,\"DownloadBytes\":2}}",
                     "{\"2026-09-30\":{\"UploadBytes\":-1,\"DownloadBytes\":2}}" })
        {
            string damagedPath = Path.Combine(directory, "damaged.json");
            File.WriteAllText(damagedPath, bad);
            using var damaged = new TrafficStatistics(damagedPath);
            damaged.Record(1, 2, time, 0);
            damaged.Save(force: true);
            check(damaged.GetSnapshot(time, 0).StorageError.Length > 0
                && File.ReadAllText(damagedPath) == bad, "traffic: corrupt history remains recoverable");
        }

        string blockedPath = Path.Combine(directory, "blocked");
        using var blocked = new TrafficStatistics(blockedPath);
        Directory.CreateDirectory(blockedPath);
        blocked.Record(11, 22, time, 0);
        blocked.Save(force: true);
        check(blocked.GetSnapshot(time, 0).StorageError.Length > 0
            && blocked.GetSnapshot(time, 0).AllTime == new TrafficTotals(11, 22),
            "traffic: write failure preserves in-memory counters and reports error");
        Directory.Delete(blockedPath);
        blocked.Save(force: true);
        check(blocked.GetSnapshot(time, 0).StorageError.Length == 0 && File.Exists(blockedPath),
            "traffic: failed persistence can be retried");

        using var service = new ClashWebSocketService();
        int events = 0;
        long receivedUpload = 0, receivedDownload = 0;
        int connectionCount = -1;
        service.OnTrafficTotalsReceived += (up, down) => { events++; receivedUpload = up; receivedDownload = down; };
        service.OnConnectionsReceived += list => connectionCount = list.Count;
        void Message(string json) => service.ProcessMessage(Encoding.UTF8.GetBytes(json));
        Message("{\"uploadTotal\":3000000000,\"downloadTotal\":4000000000,\"connections\":null}");
        check(events == 1 && receivedUpload == 3000000000 && receivedDownload == 4000000000
            && connectionCount == 0, "traffic: 64-bit totals with no active connections");
        Message("{\"uploadTotal\":10,\"downloadTotal\":20,\"connections\":[]}");
        check(events == 2 && receivedUpload == 10 && receivedDownload == 20,
            "traffic: empty connection array still reports cumulative traffic");
        Message("{\"connections\":[]}");
        Message("{\"uploadTotal\":-1,\"downloadTotal\":20}");
        Message("{\"uploadTotal\":\"invalid\",\"downloadTotal\":20}");
        Message("{\"uploadTotal\":1e30,\"downloadTotal\":20}");
        Message("null");
        check(events == 2, "traffic: missing or invalid totals do not reset the baseline");
    }
}
