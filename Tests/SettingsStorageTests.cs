using singC.Helpers;
using singC.Models;

internal static class SettingsStorageTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var loads = new ConfigLoadCoordinator();
        var oldRead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = loads.ReadLatestAsync("A", _ => oldRead.Task);
        var newer = await loads.ReadLatestAsync("B", _ => Task.FromResult("new config"));
        oldRead.SetResult("old config");
        check(newer == "new config" && await older == null, "config: old completion cannot overwrite new selection");
        var pendingRead = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beforeEdit = loads.ReadLatestAsync("B", _ => pendingRead.Task);
        loads.Invalidate(); pendingRead.SetResult("disk content");
        check(await beforeEdit == null, "config: editing or saving invalidates pending disk load");
        var failing = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleError = loads.ReadLatestAsync("A", _ => failing.Task);
        loads.Invalidate(); failing.SetException(new IOException("old failure"));
        check(await staleError == null, "config: old read error cannot replace current status");
        try { await loads.ReadLatestAsync("B", _ => throw new IOException("current failure")); throw new Exception("Expected current error"); }
        catch (IOException ex) { check(ex.Message == "current failure", "config: current read error surfaces"); }
        string absent = Path.Combine(root, "absent-directory", "config.json");
        check(ConfigFileStore.GetBackups(absent).Count == 0, "config: missing backup directory is empty");
        ConfigFileStore.CreateBackup(absent);
        check(!Directory.Exists(Path.GetDirectoryName(absent)), "config: missing source creates no backup directory");
        string nestedSettings = Path.Combine(root,"nested","settings.json");
        new SettingsStore(nestedSettings).Set("first","saved");
        check(new SettingsStore(nestedSettings).Get("first") == "saved", "settings: first write creates missing parent");
        File.WriteAllText(nestedSettings,"{\"existing\":\"keep\"}");
        new SettingsStore(nestedSettings).Set("new","added");
        check(new SettingsStore(nestedSettings).Get("existing") == "keep", "settings: first operation write loads existing keys");
        File.WriteAllText(nestedSettings,"{\"good\":\"value\",\"bad\":null}");
        check(new SettingsStore(nestedSettings).ReadError.Contains("已保留原文件"), "settings: error query loads and protects mixed invalid values");
        var path = Path.Combine(root, "settings.json");
        var store = new SettingsStore(path);
        check(store.Get("missing") == null && store.ReadError == "", "settings: missing file starts empty");
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => store.Set("key" + i, i.ToString()))));
        var restored = new SettingsStore(path);
        check(Enumerable.Range(0, 32).All(i => restored.Get("key" + i) == i.ToString()), "settings: concurrent writes retain every key after restart");
        store.SetList("paths", ["A", "a", "", "  ", "B"]);
        check(store.GetList("paths").SequenceEqual(["A", "B"]), "settings: lists filter blanks and duplicates");
        foreach (string value in new[] { "", "  ", "null", "{}", "[", "[null,\"ok\"]" })
        {
            store.Set("list", value);
            check(store.GetList("list").SequenceEqual(value.Contains("ok") ? new[] { "ok" } : Array.Empty<string>()), "settings: list parsing " + value);
        }
        var original = store.Get("key0");
        File.Delete(path);
        Directory.CreateDirectory(path);
        try { store.Set("key0", "changed"); throw new Exception("Expected write failure"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { store.SetList("paths", ["changed"]); throw new Exception("Expected list write failure"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        check(store.Get("key0") == original && store.GetList("paths").SequenceEqual(["A", "B"]), "settings: failed scalar/list writes leave memory unchanged");
        check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "settings: failed writes clean temporary files");
        Directory.Delete(path);
        store.Set("recovered", "yes");
        check(new SettingsStore(path).Get("recovered") == "yes", "settings: transient write failure can recover");
        foreach (string corrupt in new[] { "{", "null", "{\"x\":null}", "{\"x\":123}" })
        {
            File.WriteAllText(path, corrupt);
            var broken = new SettingsStore(path);
            check(broken.Get("x") == null && broken.ReadError.Length > 0, "settings: corrupt file reports error " + corrupt);
            try { broken.Set("x", "replace"); throw new Exception("Expected protected file"); } catch (IOException) { }
            check(File.ReadAllText(path) == corrupt, "settings: corrupt original is preserved " + corrupt);
        }
        string config = Path.Combine(root, "config.json");
        check(ConfigFileStore.GetBackups(config).Count == 0, "config: empty backup list");
        for (int i = 0; i < 15; i++)
        {
            await File.WriteAllTextAsync(config, "version-" + i);
            ConfigFileStore.CreateBackup(config);
        }
        check(ConfigFileStore.GetBackups(config).Count == 10 && File.ReadAllText(config + ".bak") == "version-14", "config: only ten backups retained after fifteen saves");
        string ordered = Path.Combine(root,"ordered.json");
        string oldBackup=ordered+".bak-a", tieLow=ordered+".bak-b", tieHigh=ordered+".bak-c";
        foreach(string backup in new[]{oldBackup,tieLow,tieHigh}) File.WriteAllText(backup,"backup");
        File.SetLastWriteTimeUtc(oldBackup,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(tieLow,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(tieHigh,new DateTime(2021,1,1,0,0,0,DateTimeKind.Utc));
        File.WriteAllText(ordered+".unrelated","keep");
        check(ConfigFileStore.GetBackups(ordered).SequenceEqual(new[]{tieHigh,tieLow,oldBackup}), "config: backups sort newest first with deterministic ties and exact prefix");
        string unrelated = Path.Combine(root, "another.json.bak-old");
        File.WriteAllText(unrelated, "keep");
        ConfigFileStore.CreateBackup(config);
        check(File.ReadAllText(unrelated) == "keep", "config: backup pruning leaves unrelated files intact");
        await ConfigFileStore.WriteAsync(config, "new");
        check(File.ReadAllText(config) == "new", "config: atomic replacement publishes content");
        check(File.ReadAllBytes(config).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("new")), "config: saved text has no UTF8 BOM");
        string blocked = Path.Combine(root,"blocked-config"); Directory.CreateDirectory(blocked);
        try { await ConfigFileStore.WriteAsync(blocked,"replacement"); throw new Exception("Expected replacement failure"); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        check(Directory.Exists(blocked) && !Directory.EnumerateFiles(root,"*.tmp").Any(), "config: failed replacement cleans temporary data");
        try { await ConfigFileStore.WriteAsync(config, "cancelled", new CancellationToken(true)); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        check(File.ReadAllText(config) == "new" && !Directory.EnumerateFiles(root, "*.tmp").Any(), "config: cancellation retains original and cleans temporary file");
        string selected = config, second = Path.Combine(root, "second.json");
        File.WriteAllText(second, "B");
        var pending = new TaskCompletionSource<ConfigValidationResult>();
        var save = ConfigFileStore.SaveValidatedAsync(selected, "A-new", (p, text) =>
        {
            check(p == Path.GetFullPath(config) && text == "A-new", "config: validation uses captured path and content");
            return pending.Task;
        });
        selected = second;
        pending.SetResult(ConfigValidationResult.Success("ok"));
        await save;
        check(File.ReadAllText(config) == "A-new" && File.ReadAllText(selected) == "B", "config: selection change during validation cannot redirect the write");
        check(File.ReadAllText(config+".bak") == "new", "config: validated save backs up pre-save contents");
        var rejected = await ConfigFileStore.SaveValidatedAsync(config, "bad", (_, _) => Task.FromResult(ConfigValidationResult.Failure("test", "invalid")));
        check(!rejected.IsValid && File.ReadAllText(config) == "A-new", "config: failed validation preserves file");
    }
}
