using System;
using System.IO;
using System.Text.Json;

namespace singC.Helpers;

public sealed record SystemProxySettings(int Flags, string Server, string Bypass, string AutoConfigUrl);
public interface ISystemProxyBackend
{
    SystemProxySettings Read();
    void Write(SystemProxySettings settings);
}

// An exclusive file handle prevents two singC instances from claiming the proxy.
// The journal survives crashes; recovery changes settings only if they are still ours.
public sealed class SystemProxyLease(ISystemProxyBackend backend, string path)
{
    private sealed record Journal(SystemProxySettings Original, SystemProxySettings Applied);
    private FileStream? _stream;
    private readonly object _gate = new();

    public void Recover()
    {
        lock (_gate) RecoverCore();
    }

    private void RecoverCore()
    {
        if (!File.Exists(path)) return;
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { return; } // another instance owns it
        using (stream) RestoreJournal(stream);
    }

    public void Apply(string? host, int? port)
    {
        lock (_gate) ApplyCore(host, port);
    }

    private void ApplyCore(string? host, int? port)
    {
        if (_stream != null) throw new InvalidOperationException("系统代理尚未恢复，不能重复设置。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            RestoreJournal(stream);
            var original = backend.Read();
            string server = host?.Contains(':') == true ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";
            var applied = port.HasValue
                ? new SystemProxySettings(3, $"http={server};https={server}", "<local>;localhost;127.*;[::1]", "")
                : original with { Flags = 1 }; // TUN temporarily disables manual/PAC/auto-detect proxying.
            stream.Position = 0;
            JsonSerializer.Serialize(stream, new Journal(original, applied));
            stream.Flush(flushToDisk: true);
            try { backend.Write(applied); }
            catch (Exception applyError)
            {
                // A partially applied native write also needs rollback.
                try { backend.Write(original); }
                catch (Exception restoreError)
                {
                    throw new AggregateException("设置系统代理失败，恢复原设置也失败；已保留恢复记录。", applyError, restoreError);
                }
                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
                throw;
            }
            _stream = stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public void Restore()
    {
        lock (_gate) RestoreCore();
    }

    private void RestoreCore()
    {
        var stream = _stream;
        _stream = null;
        if (stream == null) { RecoverCore(); return; }
        using (stream) RestoreJournal(stream);
    }

    private void RestoreJournal(FileStream stream)
    {
        if (stream.Length == 0) return;
        stream.Position = 0;
        var journal = JsonSerializer.Deserialize<Journal>(stream)
            ?? throw new InvalidDataException("系统代理恢复记录无效，请检查 system-proxy-backup.json。");
        if (journal.Original == null || journal.Applied == null) throw new InvalidDataException("系统代理恢复记录不完整。");
        if (backend.Read() == journal.Applied) backend.Write(journal.Original);
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
    }
}
