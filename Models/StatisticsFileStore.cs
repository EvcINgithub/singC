using System;
using System.IO;
using System.Text.Json;

namespace singC.Models;

// The lease covers loading and every later save, not just the atomic rename.
// A second instance may read history but cannot replace it using a stale snapshot.
internal sealed class StatisticsFileStore<T> : IDisposable where T : class, new()
{
    private readonly string _path;
    private readonly Func<double> _seconds;
    private FileStream? _lease;
    private bool _canSave;
    private bool _disposed;
    private double _lastSave = double.NegativeInfinity;
    public string Error { get; private set; } = "";

    public StatisticsFileStore(string path, Func<double>? seconds = null)
    {
        _path = Path.GetFullPath(path);
        _seconds = seconds ?? (() => TrafficStatistics.MonotonicSeconds);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _canSave = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "统计存储只读，无法取得独占写入权：" + ex.Message; }
    }

    public T Load(Func<T, bool> validate)
    {
        try
        {
            var data = JsonSerializer.Deserialize<T>(File.ReadAllText(_path));
            if (data == null || !validate(data)) throw new InvalidDataException("统计文件格式无效。");
            return data;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _canSave = false;
            Error = "历史统计读取失败，已保留原文件，本次数据暂不保存：" + ex.Message;
            return new();
        }
    }

    public bool TrySave(T value, bool force)
    {
        if (_disposed || !_canSave) return false;
        double now = _seconds();
        if (!force && now - _lastSave < 15) return false;
        _lastSave = now;
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            Error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "统计保存失败，将自动重试：" + ex.Message; return false; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    public void Dispose()
    {
        _disposed = true;
        _lease?.Dispose();
        _lease = null;
    }
}
