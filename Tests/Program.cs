using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using singC.Updates;

var root = Path.Combine(Path.GetTempPath(), "singC-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or OperationCanceledException) { Check(true, name); return; }
    throw new Exception("Expected rejection: " + name);
}
string Dir(string name) { var p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
string Hash(string p) { using var s = File.OpenRead(p); return Convert.ToHexString(SHA256.HashData(s)); }
void Fixture(string dir, string version, params string[] extras)
{
    var names = new[] { "singC.exe", "singC.dll", "singC.pri", "singC.Updater.exe" }.Concat(extras).ToArray();
    foreach (var name in names) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!); File.WriteAllText(Path.Combine(dir, name), version + name); }
    var manifest = new PackageManifest(version, "win-x64", names.Select(n => new PackageFile(n, Hash(Path.Combine(dir, n)))).ToArray());
    File.WriteAllText(Path.Combine(dir, UpdatePackage.ManifestName), JsonSerializer.Serialize(manifest));
}
try
{
    string Json(string tag = "v1.0.2", bool prerelease = false, string owner = "EvcINgithub/singC") => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, prerelease, body = "release notes",
        assets = new[] { "singC-1.0.2-win-x64.zip", "singC-1.0.2-win-x64.zip.sha256" }.Select(n => new { name = n, browser_download_url = $"https://github.com/{owner}/releases/download/{tag}/{n}" })
    });
    Check(ReleaseInfo.Parse(Json(), new Version(1, 0, 1))?.Version == new Version(1, 0, 2), "new release");
    Check(ReleaseInfo.Parse(Json(), new Version(1, 0, 2, 0)) == null, "same version normalized");
    Check(ReleaseInfo.Parse(Json(), new Version(2, 0, 0)) == null, "no downgrade");
    Check(ReleaseInfo.Parse(Json(prerelease: true), new Version(1, 0, 1)) == null, "ignore prerelease");
    Reject(() => ReleaseInfo.Parse(Json(owner: "other/repo"), new Version(1, 0, 1)), "reject foreign asset URL");
    Reject(() => ReleaseInfo.ParseHash(new string('a', 64) + "  wrong.zip", "right.zip"), "reject wrong hash filename");
    var stage = Dir("stage"); Fixture(stage, "1.0.2", "Assets/icon.png");
    Check(UpdatePackage.ValidateDirectory(stage, "1.0.2").Files.Length == 5, "valid manifest");
    var zip = Path.Combine(root, "update.zip"); ZipFile.CreateFromDirectory(stage, zip);
    var extracted = Path.Combine(root, "extracted");
    UpdatePackage.Extract(zip, extracted, Hash(zip), "1.0.2", default);
    Check(File.Exists(Path.Combine(extracted, "singC.exe")), "extract verified archive");
    Reject(() => UpdatePackage.Extract(zip, Dir("badhash"), new string('0', 64), "1.0.2", default), "reject corrupt download");
    Reject(() => UpdatePackage.ValidateDirectory(stage, "1.0.3"), "reject mismatched version");
    Reject(() => UpdatePackage.SafePath(stage, "../escape"), "reject traversal");
    Reject(() => UpdatePackage.SafePath(stage, "C:/escape"), "reject absolute path");
    Reject(() => UpdatePackage.SafePath(stage, "file:stream"), "reject alternate data stream");
    Reject(() => UpdatePackage.CheckManagedPath("settings.json"), "protect user settings");
    Reject(() => UpdatePackage.CheckManagedPath("sing-box.exe"), "protect proxy executable");
    File.WriteAllText(Path.Combine(stage, "singC.dll"), "tampered");
    Reject(() => UpdatePackage.ValidateDirectory(stage, "1.0.2"), "reject tampered file");
    Fixture(stage, "1.0.2", "Assets/icon.png");
    File.WriteAllText(Path.Combine(stage, "extra.txt"), "unexpected");
    Reject(() => UpdatePackage.ValidateDirectory(stage, "1.0.2"), "reject unlisted file");
    File.Delete(Path.Combine(stage, "extra.txt"));
    Reject(() => UpdatePackage.Extract(zip, Dir("cancelled"), Hash(zip), "1.0.2", new CancellationToken(true)), "cancel extraction");
    var target = Dir("target"); Fixture(target, "1.0.1", "obsolete.dll");
    File.WriteAllText(Path.Combine(target, "settings.json"), "private settings");
    var transaction = new InstallTransaction(target, Dir("backup"));
    transaction.Apply(stage, "1.0.2");
    Check(File.ReadAllText(Path.Combine(target, "singC.exe")).StartsWith("1.0.2"), "replace installed files");
    Check(!File.Exists(Path.Combine(target, "obsolete.dll")), "remove obsolete managed files");
    Check(File.ReadAllText(Path.Combine(target, "settings.json")) == "private settings", "preserve user files");
    transaction.Rollback();
    Check(File.ReadAllText(Path.Combine(target, "singC.exe")).StartsWith("1.0.1") && File.Exists(Path.Combine(target, "obsolete.dll")), "rollback after startup failure");
    Check(!File.Exists(Path.Combine(target, "Assets/icon.png")), "rollback removes new files");
    var failure = new InstallTransaction(target, Dir("failure-backup"));
    int copies = 0;
    Reject(() => failure.Apply(stage, "1.0.2", _ => { if (++copies == 3) throw new IOException("simulated write failure"); }), "mid-install failure");
    Check(File.ReadAllText(Path.Combine(target, "singC.exe")).StartsWith("1.0.1"), "mid-install automatic rollback");
    Console.WriteLine($"{passed} checks passed.");
    return 0;
}
finally
{
    // The uniquely created test root is the only directory removed.
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
}
