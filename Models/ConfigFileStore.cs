using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models;

public static class ConfigFileStore
{
    public const int RetainedBackups = 10;
    public static async Task<ConfigValidationResult> SaveValidatedAsync(string path, string text,
        Func<string, string, Task<ConfigValidationResult>> validate)
    {
        string target = Path.GetFullPath(path);
        var result = await validate(target, text);
        if (!result.IsValid) return result;
        CreateBackup(target);
        await WriteAsync(target, text);
        return result;
    }
    public static IReadOnlyList<string> GetBackups(string path)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        string prefix = Path.GetFileName(full) + ".bak-";
        return Directory.EnumerateFiles(directory)
            .Where(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    public static void CreateBackup(string path)
    {
        if (!File.Exists(path)) return;
        File.Copy(path, path + ".bak", overwrite: true);
        File.Copy(path, path + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N"));
        foreach (string old in GetBackups(path).Skip(RetainedBackups)) File.Delete(old);
    }

    public static async Task WriteAsync(string path, string text, CancellationToken token = default)
    {
        string target = Path.GetFullPath(path);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
