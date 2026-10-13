using singC.Helpers;

internal static class LoggingSettingsTests
{
    private sealed class Backend : ISystemProxyBackend
    {
        public SystemProxySettings Current = new(1, "", "", "");
        public SystemProxySettings Read() => Current;
        public void Write(SystemProxySettings settings) => Current = settings;
    }

    public static void Run(string root, Action<bool, string> check)
    {
        string settingsPath = Path.Combine(root, "settings.json");
        var store = new SettingsStore(settingsPath);
        var preference = new LoggingSettings(store);
        check(!preference.Enabled && !File.Exists(settingsPath), "logging preference: default is off without creating settings");
        foreach (var sample in new[] { ("", false), ("invalid", false), ("False", false), ("true", true), ("TRUE", true) })
        {
            store.Set(LoggingSettings.EnabledKey, sample.Item1);
            check(preference.Enabled == sample.Item2, "logging preference: interprets " + sample.Item1);
        }
        store.Set("UnrelatedSetting", "preserved");
        preference.Enabled = true;
        check(new LoggingSettings(new SettingsStore(settingsPath)).Enabled, "logging preference: enabled value survives restart");
        preference.Enabled = false;
        check(!new LoggingSettings(new SettingsStore(settingsPath)).Enabled && store.Get("UnrelatedSetting") == "preserved",
            "logging preference: disabled value survives restart and preserves other settings");

        string journal = Path.Combine(root, "lease.json");
        string log = Path.Combine(root, "proxy-diagnostics.jsonl");
        var backend = new Backend();
        var original = backend.Current;
        var lease = new SystemProxyLease(backend, journal, () => preference.Enabled);
        lease.Apply("127.0.0.1", 7890);
        check(!File.Exists(log) && new FileInfo(journal).Length > 0, "logging preference: off suppresses logs but retains recovery backup");
        preference.Enabled = true;
        lease.Trace("test.enabled-live");
        check(File.ReadAllText(log).Contains("test.enabled-live"), "logging preference: enabling takes effect immediately");
        var before = File.ReadAllBytes(log);
        preference.Enabled = false;
        lease.Restore();
        check(before.SequenceEqual(File.ReadAllBytes(log)) && backend.Current == original,
            "logging preference: disabling stops appends without deleting logs or preventing restoration");

        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try { preference.Enabled = true; throw new Exception("Expected failed save"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { check(!preference.Enabled, "logging preference: failed save cannot enable logging in memory"); }
        }
        string corruptPath = Path.Combine(root, "corrupt.json");
        File.WriteAllText(corruptPath, "{invalid");
        var corrupt = new LoggingSettings(new SettingsStore(corruptPath));
        check(!corrupt.Enabled, "logging preference: corrupt settings default to off");
        try { corrupt.Enabled = true; throw new Exception("Expected corrupt settings rejection"); }
        catch (IOException) { check(File.ReadAllText(corruptPath) == "{invalid", "logging preference: unreadable settings are preserved"); }
    }
}
