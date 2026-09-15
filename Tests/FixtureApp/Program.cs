// A windowless process used only to verify the real updater's process handoff.
if (args.Contains("--fixture-parent"))
{
    await Task.Delay(TimeSpan.FromSeconds(60));
    return;
}
int index = Array.IndexOf(args, "--update-ready");
if (index >= 0 && index + 1 < args.Length)
{
    File.WriteAllText(args[index + 1], Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(60));
}
