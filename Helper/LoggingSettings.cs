namespace singC.Helpers;

// Shared preference for current and future diagnostic log outputs.
public sealed class LoggingSettings(SettingsStore store)
{
    public const string EnabledKey = "LoggingEnabled";
    public bool Enabled
    {
        get => bool.TryParse(store.Get(EnabledKey), out bool enabled) && enabled;
        set => store.Set(EnabledKey, value.ToString());
    }
}
