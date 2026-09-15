using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace singC.Updates;

public sealed record PackageFile(string Path, string Sha256);
public sealed record PackageManifest(string Version, string Architecture, PackageFile[] Files);

public static class UpdatePackage
{
    public const string ManifestName = "update-manifest.json";
    public static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/'))
            throw new InvalidDataException("更新包路径无效。");
        var parts = relative.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("更新包路径无效。");
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包路径超出目标目录。");
        // Never follow an existing junction or symbolic link during installation.
        for (var item = new DirectoryInfo(fullRoot); item != null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("更新目录包含链接，请移至普通目录。");
        var cursor = fullRoot;
        foreach (var part in parts)
        {
            cursor = Path.Combine(cursor, part);
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && File.GetAttributes(cursor).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("更新目标包含链接。");
        }
        return path;
    }

    public static void CheckManagedPath(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Split('/').Any(p => p is ".git" or "bin" or "obj" || p.EndsWith(".exe.webview2")) ||
            lower.EndsWith(".db") || lower.EndsWith(".pfx") || lower.EndsWith(".key") ||
            Path.GetFileName(lower) is "settings.json" or "config.json" or "cookies" or "history" or "sing-box.exe")
            throw new InvalidDataException("更新包不得覆盖用户数据或 sing-box。");
    }

    public static PackageManifest ReadManifest(string directory)
    {
        var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(directory, ManifestName)))
            ?? throw new InvalidDataException("缺少更新清单。");
        ReleaseInfo.ParseVersion(manifest.Version);
        if (manifest.Architecture != "win-x64" || manifest.Files is null || manifest.Files.Length is < 1 or > 10000)
            throw new InvalidDataException("更新包架构或清单无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            SafePath(directory, file.Path);
            CheckManagedPath(file.Path);
            if (!names.Add(file.Path) || file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新清单包含重复文件。");
        }
        foreach (var required in new[] { "singC.exe", "singC.dll", "singC.pri", "singC.Updater.exe" })
            if (!names.Contains(required)) throw new InvalidDataException("更新包缺少必要程序文件。");
        return manifest;
    }

    public static PackageManifest ValidateDirectory(string directory, string expectedVersion, CancellationToken token = default)
    {
        var manifest = ReadManifest(directory);
        if (manifest.Version != expectedVersion) throw new InvalidDataException("更新包版本与发布版本不一致。");
        foreach (var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(SafePath(directory, file.Path));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包文件校验失败。");
        }
        if (Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count() != manifest.Files.Length + 1)
            throw new InvalidDataException("更新包包含未列入清单的文件。");
        return manifest;
    }

    public static void Extract(string zipPath, string stage, string hash, string version, CancellationToken token)
    {
        using (var stream = File.OpenRead(zipPath))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包 SHA-256 校验失败。");
        Directory.CreateDirectory(stage);
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("更新包文件过多。");
        long length = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/');
            var name = isDirectory ? entry.FullName.TrimEnd('/') : entry.FullName;
            var path = SafePath(stage, name);
            if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("更新包包含重复路径或符号链接。");
            length = checked(length + entry.Length);
            if (length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包解压大小超出限制。");
            if (isDirectory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
        ValidateDirectory(stage, version, token);
    }
}
