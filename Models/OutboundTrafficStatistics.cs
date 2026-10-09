using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace singC.Models;

public enum OutboundTrafficPeriod { Session, Today, Month, AllTime }
public sealed record ConnectionTrafficSample(string Id, DateTimeOffset StartedAt, string? Outbound,
    long UploadBytes, long DownloadBytes);
public sealed record OutboundTrafficRow(string Tag, TrafficTotals Totals)
{
    public string Name => Tag.Length == 0 ? "未知出站" : Tag;
}
public sealed record OutboundTrafficSnapshot(IReadOnlyList<OutboundTrafficRow> Rows, string StorageError);

// Connection snapshots are a lower-bound estimate, independent of the core's total counters.
public sealed class OutboundTrafficStatistics : IDisposable
{
    private readonly object _gate = new();
    private readonly StatisticsFileStore<Dictionary<string, Dictionary<string, TrafficTotals>>> _store;
    private Dictionary<string, Dictionary<string, TrafficTotals>> _days = new();
    private readonly Dictionary<string, TrafficTotals> _session = new(StringComparer.Ordinal);
    private Dictionary<(string Id, DateTimeOffset Start), ConnectionTrafficSample> _previous = new();
    private DateTimeOffset? _lastFrame;
    private bool _dirty;

    public OutboundTrafficStatistics(string path)
    {
        _store = new(path);
        _days = _store.Load(ValidateHistory);
    }

    private static bool ValidateHistory(Dictionary<string, Dictionary<string, TrafficTotals>> days)
    {
            foreach (var day in days)
                if (!DateOnly.TryParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    || day.Value == null || day.Value.Any(item => item.Value == null
                        || item.Value.UploadBytes < 0 || item.Value.DownloadBytes < 0)) return false;
            return true;
    }

    public void BeginSession()
    {
        lock (_gate)
        {
            _session.Clear();
            _previous.Clear();
            _lastFrame = null;
        }
    }

    public void Record(IReadOnlyList<ConnectionTrafficSample> connections, DateTimeOffset now)
    {
        lock (_gate)
        {
            var next = new Dictionary<(string, DateTimeOffset), ConnectionTrafficSample>();
            string date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var sample in connections)
            {
                if (string.IsNullOrWhiteSpace(sample.Id) || sample.UploadBytes < 0 || sample.DownloadBytes < 0
                    || sample.StartedAt == default || sample.StartedAt > now) continue;
                var key = (sample.Id, sample.StartedAt);
                if (!next.TryAdd(key, sample)) continue;
                long up = 0, down = 0;
                if (_previous.TryGetValue(key, out var previous))
                {
                    // A counter rollback resets both baselines, rather than adding old bytes again.
                    if (sample.UploadBytes >= previous.UploadBytes && sample.DownloadBytes >= previous.DownloadBytes)
                    {
                        up = sample.UploadBytes - previous.UploadBytes;
                        down = sample.DownloadBytes - previous.DownloadBytes;
                    }
                }
                else if (_lastFrame is { } last && sample.StartedAt > last)
                {
                    up = sample.UploadBytes;
                    down = sample.DownloadBytes;
                }
                if (up == 0 && down == 0) continue;
                string tag = string.IsNullOrWhiteSpace(sample.Outbound) ? "" : sample.Outbound;
                var delta = new TrafficTotals(up, down);
                Accumulate(_session, tag, delta);
                if (!_days.TryGetValue(date, out var day)) _days[date] = day = new(StringComparer.Ordinal);
                Accumulate(day, tag, delta);
                _dirty = true;
            }
            // Only retain live baselines. A reappearing older connection is baselined, never recounted.
            _previous = next;
            if (_lastFrame == null || now > _lastFrame) _lastFrame = now;
        }
    }

    public OutboundTrafficSnapshot GetSnapshot(OutboundTrafficPeriod period, DateTimeOffset now)
    {
        lock (_gate)
        {
            var totals = new Dictionary<string, TrafficTotals>(StringComparer.Ordinal);
            string date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (period == OutboundTrafficPeriod.Session)
                foreach (var item in _session) totals[item.Key] = item.Value;
            else
                foreach (var day in _days)
                {
                    if (period == OutboundTrafficPeriod.Today && day.Key != date) continue;
                    if (period == OutboundTrafficPeriod.Month && !day.Key.StartsWith(date[..7], StringComparison.Ordinal)) continue;
                    foreach (var item in day.Value) Accumulate(totals, item.Key, item.Value);
                }
            return new(totals.Select(item => new OutboundTrafficRow(item.Key, item.Value))
                .OrderByDescending(row => (decimal)row.Totals.UploadBytes + row.Totals.DownloadBytes)
                .ThenBy(row => row.Tag, StringComparer.Ordinal).ToArray(), _store.Error);
        }
    }

    public void Save(bool force = false)
    {
        lock (_gate)
            if (_dirty && _store.TrySave(_days, force)) _dirty = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Save(force: true);
            _store.Dispose();
        }
    }

    private static void Accumulate(Dictionary<string, TrafficTotals> totals, string tag, TrafficTotals delta)
    {
        var before = totals.GetValueOrDefault(tag) ?? new();
        totals[tag] = new(Add(before.UploadBytes, delta.UploadBytes), Add(before.DownloadBytes, delta.DownloadBytes));
    }
    private static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
