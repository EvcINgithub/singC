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
public sealed class SystemProxyLease(ISystemProxyBackend backend, string path, Func<bool>? isLoggingEnabled = null)
{
    private sealed record Journal(SystemProxySettings Original, SystemProxySettings Applied);
    private FileStream? _stream;
    private readonly object _gate = new();
    private readonly string _session = Guid.NewGuid().ToString("N");

    internal void Trace(string stage, Exception? error = null) =>
        ProxyDiagnosticLog.Write(path, _session, stage, error: error, isEnabled: isLoggingEnabled);

    public void Recover()
    {
        lock (_gate)
        {
            Trace("recover.requested");
            try { RecoverCore(); }
            catch (Exception ex) { Trace("recover.failed", ex); throw; }
        }
    }

    private void RecoverCore()
    {
        if (!File.Exists(path)) { Trace("recover.no-journal"); return; }
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 32)
        { Trace("recover.active-lease", ex); return; } // another instance owns it
        using (stream) RestoreJournal(stream);
    }

    public void Apply(string? host, int? port)
    {
        lock (_gate)
        {
            Trace("apply.requested");
            try { ApplyCore(host, port); }
            catch (Exception ex) { Trace("apply.failed", ex); throw; }
        }
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
            ProxyDiagnosticLog.Write(path, _session, "apply.journal-saved", original, applied, isEnabled: isLoggingEnabled);
            try { backend.Write(applied); }
            catch (Exception applyError)
            {
                Trace("apply.write-failed", applyError);
                // A partially applied native write also needs rollback.
                try { backend.Write(original); }
                catch (Exception restoreError)
                {
                    Trace("apply.rollback-failed", restoreError);
                    throw new AggregateException("设置系统代理失败，恢复原设置也失败；已保留恢复记录。", applyError, restoreError);
                }
                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
                Trace("apply.rollback-completed");
                throw;
            }
            _stream = stream;
            Trace("apply.completed");
        }
        catch { stream.Dispose(); throw; }
    }

    public void Restore()
    {
        lock (_gate)
        {
            Trace("restore.requested");
            try { RestoreCore(); Trace("restore.completed"); }
            catch (Exception ex) { Trace("restore.failed", ex); throw; }
        }
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
        if (stream.Length == 0) { Trace("restore.empty-journal"); return; }
        stream.Position = 0;
        var journal = JsonSerializer.Deserialize<Journal>(stream)
            ?? throw new InvalidDataException("系统代理恢复记录无效，请检查 system-proxy-backup.json。");
        if (journal.Original == null || journal.Applied == null) throw new InvalidDataException("系统代理恢复记录不完整。");
        var current = backend.Read();
        ProxyDiagnosticLog.Write(path, _session, "restore.compare", journal.Original, journal.Applied, current, isEnabled: isLoggingEnabled);
        if (current == journal.Applied)
        {
            Trace("restore.write-original");
            backend.Write(journal.Original);
            Trace("restore.write-succeeded");
        }
        else Trace("restore.skipped-settings-changed");
        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
        Trace("restore.journal-cleared");
    }
}
