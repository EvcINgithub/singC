using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace singC.Helpers;

// A single store owns the in-process read/modify/write transaction. Failed writes
// never publish a new in-memory value; unreadable files remain available for recovery.
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, string>? _values;
    private string _readError = "";
    public SettingsStore(string path) => _path = Path.GetFullPath(path);
    public string ReadError { get { lock (_gate) { Load(); return _readError; } } }

    public string? Get(string key)
    {
        lock (_gate) { Load(); return _values!.GetValueOrDefault(key); }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            Load();
            if (_readError.Length != 0) throw new IOException(_readError);
            var next = new Dictionary<string, string>(_values!) { [key] = value };
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, next);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
                _values = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public List<string> GetList(string key)
    {
        string? text = Get(key);
        if (string.IsNullOrWhiteSpace(text)) return new();
        try { return Clean(JsonSerializer.Deserialize<List<string>>(text) ?? new()); }
        catch (JsonException) { return new(); }
    }

    public void SetList(string key, IEnumerable<string> values) => Set(key, JsonSerializer.Serialize(Clean(values)));
    private static List<string> Clean(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Load()
    {
        if (_values != null) return;
        _values = new();
        try
        {
            // Read directly: File.Exists also returns false for some access failures.
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (loaded == null || loaded.Values.Any(value => value == null))
                throw new InvalidDataException("设置文件内容无效。");
            _values = loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _readError = "设置读取失败，已保留原文件并停止覆盖；请修复后重启：" + ex.Message;
        }
    }
}
