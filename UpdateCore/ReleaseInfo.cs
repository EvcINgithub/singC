using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace singC.Updates;

public sealed record ReleaseInfo(string Tag, Version Version, string Notes, Uri PackageUrl, Uri HashUrl)
{
    public const string Repository = "EvcINgithub/singC";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public string PackageName => $"singC-{Version.ToString(3)}-win-x64.zip";

    public static Version ParseVersion(string text)
    {
        if (!Regex.IsMatch(text, @"^v?\d+\.\d+\.\d+$"))
            throw new InvalidDataException("版本号必须为 v主版本.次版本.修订号。");
        return Version.Parse(text.TrimStart('v'));
    }

    public static ReleaseInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        var name = $"singC-{version.ToString(3)}-win-x64.zip";
        Uri Asset(string assetName)
        {
            var matches = root.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == assetName).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("该版本缺少完整的 x64 更新包或校验文件。");
            var uri = new Uri(matches[0].GetProperty("browser_download_url").GetString()!);
            var expected = $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";
            if (uri.AbsoluteUri != expected) throw new InvalidDataException("更新资源地址不属于指定的版本。");
            return uri;
        }
        return new(tag, version, root.TryGetProperty("body", out var notes) ? notes.GetString() ?? "" : "",
            Asset(name), Asset(name + ".sha256"));
    }

    public static string ParseHash(string text, string packageName)
    {
        var fields = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || !Regex.IsMatch(fields[0], "^[a-fA-F0-9]{64}$") || fields[1].TrimStart('*') != packageName)
            throw new InvalidDataException("更新包校验文件无效。");
        return fields[0];
    }
}
