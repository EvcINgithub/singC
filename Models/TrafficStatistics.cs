using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace singC.Models;

public sealed record TrafficTotals(long UploadBytes = 0, long DownloadBytes = 0)
{
    [JsonIgnore] public string UploadDisplay => FormatBytes(UploadBytes);
    [JsonIgnore] public string DownloadDisplay => FormatBytes(DownloadBytes);
    [JsonIgnore] public string TotalDisplay => FormatBytes((double)UploadBytes + DownloadBytes);

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{bytes:F1} {units[unit]}";
    }
}

public sealed record DailyTraffic(string Date, TrafficTotals Totals);
public sealed record TrafficStatisticsSnapshot(TrafficTotals Session, TrafficTotals Today,
    TrafficTotals Month, TrafficTotals AllTime, double UploadSpeed, double DownloadSpeed,
    DateTimeOffset? LastSample, IReadOnlyList<DailyTraffic> RecentDays, string StorageError);

// Counter deltas, rather than the active connection list, retain closed-connection traffic.
// All mutations and persistence are serialized because samples arrive on the socket thread.
public sealed class TrafficStatistics
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, TrafficTotals> _days = new();
    private TrafficTotals _session = new();
    private long _previousUpload;
    private long _previousDownload;
    private double? _previousSeconds;
    private double _uploadSpeed;
    private double _downloadSpeed;
    private DateTimeOffset? _lastSample;
    private double _lastSaveSeconds = double.NegativeInfinity;
    private bool _dirty;
    private bool _canSave = true;
    private string _storageError = string.Empty;

    public TrafficStatistics(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path)) return;
            var days = JsonSerializer.Deserialize<Dictionary<string, TrafficTotals>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("统计文件为空");
            foreach (var day in days)
            {
                if (!DateOnly.TryParseExact(day.Key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out _)
                    || day.Value == null || day.Value.UploadBytes < 0 || day.Value.DownloadBytes < 0)
                    throw new InvalidDataException("统计文件格式无效");
            }
            _days = days;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // Preserve an unreadable file for recovery rather than replacing historical usage.
            _canSave = false;
            _storageError = $"历史统计读取失败，本次数据暂不保存：{ex.Message}";
        }
    }

    public static double MonotonicSeconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public void BeginSession()
    {
        lock (_gate)
        {
            _session = new();
            _previousUpload = _previousDownload = 0;
            _previousSeconds = null;
            _lastSample = null;
            _uploadSpeed = _downloadSpeed = 0;
        }
    }

    public void MarkDisconnected()
    {
        lock (_gate)
        {
            // Keep the baseline across reconnects, but do not report a speed for the gap.
            _previousSeconds = null;
            _uploadSpeed = _downloadSpeed = 0;
        }
    }

    public void Record(long upload, long download, DateTimeOffset now, double seconds)
    {
        if (upload < 0 || download < 0 || !double.IsFinite(seconds)) return;
        lock (_gate)
        {
            bool reset = upload < _previousUpload || download < _previousDownload;
            long up = reset ? upload : upload - _previousUpload;
            long down = reset ? download : download - _previousDownload;
            double elapsed = seconds - (_previousSeconds ?? seconds);
            _uploadSpeed = !reset && elapsed > 0 ? up / elapsed : 0;
            _downloadSpeed = !reset && elapsed > 0 ? down / elapsed : 0;
            _previousUpload = upload;
            _previousDownload = download;
            _previousSeconds = seconds;
            _lastSample = now;
            _session = Add(_session, new(up, down));
            if (up == 0 && down == 0) return;
            // A delta spanning midnight or a reconnect belongs to its observation date.
            string date = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            _days.TryGetValue(date, out var totals);
            _days[date] = Add(totals ?? new(), new(up, down));
            _dirty = true;
        }
    }

    public TrafficStatisticsSnapshot GetSnapshot(DateTimeOffset now, double seconds)
    {
        lock (_gate)
        {
            string date = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string month = date[..7];
            var all = new TrafficTotals();
            var monthly = new TrafficTotals();
            foreach (var day in _days)
            {
                all = Add(all, day.Value);
                if (day.Key.StartsWith(month, StringComparison.Ordinal)) monthly = Add(monthly, day.Value);
            }
            var recent = Enumerable.Range(0, 7).Select(offset =>
            {
                string key = now.AddDays(-offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                return new DailyTraffic(key, _days.GetValueOrDefault(key) ?? new());
            }).ToArray();
            bool fresh = _previousSeconds.HasValue && seconds - _previousSeconds.Value <= 5;
            return new(_session, _days.GetValueOrDefault(date) ?? new(), monthly, all,
                fresh ? _uploadSpeed : 0, fresh ? _downloadSpeed : 0, _lastSample, recent, _storageError);
        }
    }

    public void Save(bool force = false)
    {
        lock (_gate)
        {
            double seconds = MonotonicSeconds;
            if (!_canSave || !_dirty || (!force && seconds - _lastSaveSeconds < 15)) return;
            _lastSaveSeconds = seconds;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                string temporary = _path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, _days);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _dirty = false;
                _storageError = string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _storageError = $"统计保存失败，将自动重试：{ex.Message}";
            }
        }
    }

    private static TrafficTotals Add(TrafficTotals left, TrafficTotals right) => new(
        SaturatingAdd(left.UploadBytes, right.UploadBytes), SaturatingAdd(left.DownloadBytes, right.DownloadBytes));
    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}
