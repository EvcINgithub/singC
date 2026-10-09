using System.Text.Json;
using singC.Updates;

internal static class UpdateBoundaryTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); } catch (InvalidDataException) { check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        foreach (string version in new[] { "", "1.2", "v1.2.3-beta", "1.2.3.4", "V1.2.3", "-1.2.3" })
            Reject(() => ReleaseInfo.ParseVersion(version), "release rejects nonstable version " + version);
        check(ReleaseInfo.ParseVersion("v1.2.3") == new Version(1,2,3) && ReleaseInfo.ParseVersion("1.2.3") == new Version(1,2,3), "release accepts optional lowercase v");
        string hash = new string('A',64);
        check(ReleaseInfo.ParseHash(hash + " *package.zip\n", "package.zip") == hash, "hash accepts binary marker and trailing newline");
        foreach (string value in new[] { "", hash, hash + " a b", new string('g',64) + " package.zip", new string('a',63) + " package.zip" })
            Reject(() => ReleaseInfo.ParseHash(value,"package.zip"), "hash rejects malformed fields " + value.Length);
        const string name = "singC-1.2.3-win-x64.zip";
        Dictionary<string,object?> Feed() => new() {
            ["tag_name"]="v1.2.3",["draft"]=false,["prerelease"]=false,
            ["assets"]=new[] {name,name+".sha256"}.Select(n=>new{name=n,browser_download_url=$"https://github.com/{ReleaseInfo.Repository}/releases/download/v1.2.3/{n}"}).ToArray()
        };
        var feed = Feed(); feed["draft"]=true;
        check(ReleaseInfo.Parse(JsonSerializer.Serialize(feed),new Version(1,0)) == null, "draft feed ignored independently");
        feed=Feed();
        check(ReleaseInfo.Parse(JsonSerializer.Serialize(feed),new Version(1,0))?.Notes == "", "missing release body defaults empty");
        feed["body"]=null;
        check(ReleaseInfo.Parse(JsonSerializer.Serialize(feed),new Version(1,0))?.Notes == "", "null release body defaults empty");
        feed["assets"]=Array.Empty<object>();
        Reject(()=>ReleaseInfo.Parse(JsonSerializer.Serialize(feed),new Version(1,0)), "missing assets rejected");
        var files = new[]{"singC.exe","singC.dll","singC.pri","singC.Updater.exe"}.Select(n=>new PackageFile(n,hash)).ToArray();
        void Manifest(string arch, PackageFile[] entries) => File.WriteAllText(Path.Combine(root,UpdatePackage.ManifestName),JsonSerializer.Serialize(new PackageManifest("1.2.3",arch,entries)));
        Manifest("arm64",files); Reject(()=>UpdatePackage.ReadManifest(root),"wrong manifest architecture");
        Manifest("win-x64",Array.Empty<PackageFile>()); Reject(()=>UpdatePackage.ReadManifest(root),"empty manifest rejected");
        Manifest("win-x64",null!); Reject(()=>UpdatePackage.ReadManifest(root),"null manifest entries rejected");
        Manifest("win-x64",files.Take(3).ToArray()); Reject(()=>UpdatePackage.ReadManifest(root),"required updater missing");
        Manifest("win-x64",files.Append(new PackageFile("SINGC.EXE",hash)).ToArray()); Reject(()=>UpdatePackage.ReadManifest(root),"case-insensitive duplicate file rejected");
        Manifest("win-x64",files.Append(new PackageFile(UpdatePackage.ManifestName,hash)).ToArray()); Reject(()=>UpdatePackage.ReadManifest(root),"manifest cannot list itself");
        File.WriteAllText(Path.Combine(root,UpdatePackage.ManifestName),"null"); Reject(()=>UpdatePackage.ReadManifest(root),"null manifest rejected");
    }
}
