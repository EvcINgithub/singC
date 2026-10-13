using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace singC.Helpers;

// Diagnostics must never prevent a proxy restoration. No raw PAC, bypass or exception text.
internal static class ProxyDiagnosticLog
{
    private static readonly object Gate = new();
    internal const long MaximumBytes = 1024 * 1024;

    internal static string[] ChangedFields(SystemProxySettings expected, SystemProxySettings current)
    {
        var fields = new List<string>();
        if (expected.Flags != current.Flags) fields.Add("Flags");
        if (expected.Server != current.Server) fields.Add("Server");
        if (expected.Bypass != current.Bypass) fields.Add("Bypass");
        if (expected.AutoConfigUrl != current.AutoConfigUrl) fields.Add("AutoConfigUrl");
        return fields.ToArray();
    }

    private static object? Snapshot(SystemProxySettings? settings) => settings == null ? null : new
    {
        settings.Flags,
        // Fingerprints prove which values differ without disclosing credentials or internal domains.
        Server = Fingerprint(settings.Server), Bypass = Fingerprint(settings.Bypass),
        AutoConfigUrl = Fingerprint(settings.AutoConfigUrl)
    };

    private static object Fingerprint(string? value) => new
    {
        Empty = string.IsNullOrEmpty(value), Length = value?.Length ?? 0,
        Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")))
    };

    internal static void Write(string journalPath, string session, string stage,
        SystemProxySettings? original = null, SystemProxySettings? applied = null,
        SystemProxySettings? current = null, Exception? error = null, Func<bool>? isEnabled = null)
    {
        try
        {
            if (!(isEnabled?.Invoke() ?? AppSettings.Logging.Enabled)) return;
            var line = JsonSerializer.Serialize(new
            {
                Schema = 1, TimestampUtc = DateTime.UtcNow, ProcessId = Environment.ProcessId,
                Session = session, Stage = stage,
                Original = Snapshot(original), Applied = Snapshot(applied), Current = Snapshot(current),
                ChangedFields = applied != null && current != null ? ChangedFields(applied, current) : null,
                ErrorType = error?.GetType().FullName, HResult = error?.HResult,
                NativeError = error is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null
            }) + Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
                string logPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "proxy-diagnostics.jsonl");
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= MaximumBytes)
                    File.Move(logPath, logPath + ".previous", overwrite: true);
                using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) { Debug.WriteLine("Proxy diagnostic log unavailable: " + ex.GetType().Name); }
    }
}
