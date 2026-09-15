using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace singC.Updates;

public sealed class InstallTransaction
{
    private sealed record BackupEntry(string Path, bool Existed);
    private readonly string _target;
    private readonly string _backup;
    private readonly List<BackupEntry> _entries = new();
    private bool _prepared;
    public InstallTransaction(string target, string backup) { _target = target; _backup = backup; }

    public void Apply(string stage, string version, Action<string>? beforeCopy = null)
    {
        var manifest = UpdatePackage.ValidateDirectory(stage, version);
        var incoming = manifest.Files.Select(f => f.Path).Append(UpdatePackage.ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var managed = new HashSet<string>(incoming, StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path.Combine(_target, UpdatePackage.ManifestName)))
            managed.UnionWith(UpdatePackage.ReadManifest(_target).Files.Select(f => f.Path));
        Directory.CreateDirectory(_backup);
        // Complete the entire backup before touching the installed version.
        foreach (var relative in managed)
        {
            var target = UpdatePackage.SafePath(_target, relative);
            var backup = UpdatePackage.SafePath(_backup, relative);
            bool exists = File.Exists(target);
            if (exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, false);
            }
            _entries.Add(new(relative, exists));
        }
        File.WriteAllText(Path.Combine(_backup, "rollback.json"), JsonSerializer.Serialize(_entries));
        _prepared = true;
        try
        {
            foreach (var entry in _entries)
            {
                var target = UpdatePackage.SafePath(_target, entry.Path);
                beforeCopy?.Invoke(entry.Path);
                if (incoming.Contains(entry.Path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(UpdatePackage.SafePath(stage, entry.Path), target, true);
                }
                else File.Delete(target);
            }
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    public void Rollback()
    {
        if (!_prepared) return;
        foreach (var entry in _entries)
        {
            var target = UpdatePackage.SafePath(_target, entry.Path);
            if (entry.Existed) File.Copy(UpdatePackage.SafePath(_backup, entry.Path), target, true);
            else File.Delete(target);
        }
        _prepared = false;
    }
}
