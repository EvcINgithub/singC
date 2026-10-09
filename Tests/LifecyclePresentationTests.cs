using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using singC.Helpers;
using singC.Models;
using singC.Updates;

internal static class LifecyclePresentationTests
{
    private sealed class Backend : ISystemProxyBackend
    {
        public SystemProxySettings Current = new(13, "old", "bypass", "pac");
        public int Failures;
        public SystemProxySettings Read() => Current;
        public void Write(SystemProxySettings value)
        {
            if (Failures-- > 0) throw new IOException("injected native write failure");
            Current = value;
        }
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        async Task Reject<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); } catch (T) { check(true, name); return; }
            throw new Exception("Expected " + typeof(T).Name + ": " + name);
        }
        foreach (int size in new[] { 0, 3, 4 })
        {
            using var content = new ByteArrayContent(Enumerable.Repeat((byte)'x', size).ToArray());
            check(await BoundedResponse.ReadAsync(content, 4, default) == new string('x', size), "bounded metadata accepts " + size);
        }
        using (var content = new StringContent("12345"))
            await Reject<InvalidDataException>(() => BoundedResponse.ReadAsync(content, 4, default), "metadata limit rejects extra byte");
        using (var content = new StringContent("中文", Encoding.UTF8))
            check(await BoundedResponse.ReadAsync(content, 6, default) == "中文", "metadata limit counts UTF8 bytes");
        foreach (int limit in new[] { 0, -1, int.MaxValue })
        {
            using var content = new StringContent("");
            await Reject<ArgumentOutOfRangeException>(() => BoundedResponse.ReadAsync(content, limit, default), "invalid metadata limit " + limit);
        }
        using (var content = new StringContent("x"))
            await Reject<OperationCanceledException>(() => BoundedResponse.ReadAsync(content, 4, new CancellationToken(true)), "metadata read cancellation");

        bool ready = false; int delays = 0;
        await UpdateHandoff.WaitUntilReadyAsync(() => ready, () => false, () => TimeSpan.FromSeconds(30), (duration, _) => { check(duration == TimeSpan.FromMilliseconds(100), "handoff polling interval"); delays++; ready = true; return Task.CompletedTask; }, default);
        check(delays == 1, "handoff accepts readiness at timeout boundary");
        await Reject<IOException>(() => UpdateHandoff.WaitUntilReadyAsync(() => false, () => true, () => TimeSpan.Zero, Task.Delay, default), "handoff rejects exited helper");
        await Reject<IOException>(() => UpdateHandoff.WaitUntilReadyAsync(() => false, () => false, () => TimeSpan.FromSeconds(31), Task.Delay, default), "handoff rejects timeout");
        await Reject<OperationCanceledException>(() => UpdateHandoff.WaitUntilReadyAsync(() => false, () => false, () => TimeSpan.Zero, Task.Delay, new CancellationToken(true)), "handoff cancellation");
        bool polledAfterCancel=false;
        await Reject<OperationCanceledException>(() => UpdateHandoff.WaitUntilReadyAsync(() => false, () => {polledAfterCancel=true;return true;}, () => TimeSpan.Zero, (_,_)=>Task.CompletedTask, new CancellationToken(true)), "handoff cancellation precedes helper inspection");
        check(!polledAfterCancel,"handoff never inspects helper after cancellation");
        foreach (string path in new[] { "", " ", "/absolute", "a\\b", "a:b", "a//b", "./a", "a/../b", "trailing.", "trailing ", "a?b" })
            await Reject<InvalidDataException>(() => { UpdatePackage.SafePath(root, path); return Task.CompletedTask; }, "unsafe update path " + path);
        check(UpdatePackage.SafePath(root, "Assets/icon.png") == Path.Combine(root, "Assets", "icon.png"), "safe nested update path");
        foreach (string path in new[] { ".git/config", "bin/a", "obj/a", "app.exe.webview2/a", "a.db", "a.pfx", "a.key", "CONFIG.JSON", "cookies", "history" })
            await Reject<InvalidDataException>(() => { UpdatePackage.CheckManagedPath(path); return Task.CompletedTask; }, "protected update path " + path);

        var order = new List<string>();
        await ShutdownSequence.RunAsync(() => { order.Add("stop"); return Task.CompletedTask; }, () => order.Add("save"), () => { order.Add("cleanup"); return Task.CompletedTask; });
        check(order.SequenceEqual(new[] { "stop", "save", "cleanup" }), "exit stops then saves then releases sampling");
        order.Clear();
        await Reject<IOException>(() => ShutdownSequence.RunAsync(() => throw new IOException(), () => order.Add("save"), () => { order.Add("cleanup"); return Task.CompletedTask; }), "exit propagates stop failure");
        check(order.SequenceEqual(new[] { "save" }), "stop failure still saves and leaves cleanup for retry");

        var backend = new Backend(); var original = backend.Current;
        string journal = Path.Combine(root, "lease.json");
        var lease = new SystemProxyLease(backend, journal);
        lease.Apply("::1", 1234);
        check(backend.Current.Server == "http=[::1]:1234;https=[::1]:1234", "lease brackets IPv6 endpoint");
        backend.Failures = 1;
        await Reject<IOException>(() => { lease.Restore(); return Task.CompletedTask; }, "restore failure surfaces");
        check(new FileInfo(journal).Length > 0, "failed restore preserves journal");
        lease.Restore();
        check(backend.Current == original && new FileInfo(journal).Length == 0, "restore retry recovers previous journal");
        backend.Failures = 2;
        try { lease.Apply("127.0.0.1", 1234); throw new Exception("Expected double failure"); }
        catch (AggregateException ex) { check(ex.InnerExceptions.Count == 2 && new FileInfo(journal).Length > 0, "apply and rollback failures both survive with journal"); }
        lease.Recover();
        foreach (string broken in new[] { "null", "{}", "{\"Original\":{\"Flags\":1}}", "{" })
        {
            File.WriteAllText(journal, broken);
            try { lease.Recover(); throw new Exception("Expected invalid journal"); }
            catch (Exception e) when (e is InvalidDataException or JsonException) { check(File.ReadAllText(journal) == broken && backend.Current == original, "corrupt journal remains intact: " + broken); }
        }
        File.Delete(journal);

        string source = Path.Combine(root, "config.json"); File.WriteAllText(source, "{}");
        Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ConfigCheckOutput>> runner = (_, _, _) => Task.FromResult(new ConfigCheckOutput(0, "ok", ""));
        var service = new SingBoxService(() => Environment.ProcessPath, () => source, (_, _) => { }, backend, Path.Combine(root, "core"), () => false, (info, time, token) => runner(info, time, token));
        runner = (info, time, _) => {
            check(info.ArgumentList[0] == "check" && info.ArgumentList[1] == "-c" && time == TimeSpan.FromSeconds(15), "validation preserves command and deadline");
            check(File.ReadAllText(info.ArgumentList[2]) == "{\"edited\":true}" && info.ArgumentList[2] != source, "validation uses isolated edited text");
            return Task.FromResult(new ConfigCheckOutput(0, "ok", ""));
        };
        check((await service.ValidateConfigAsync(configText: "{\"edited\":true}")).IsValid && File.ReadAllText(source) == "{}", "validation preserves source");
        check(!Directory.EnumerateFiles(root, ".singc-check-*").Any(), "validation cleans temporary source");
        runner = (_, _, _) => throw new TimeoutException();
        check((await service.ValidateConfigAsync()).Stage == "超时", "validation timeout result");
        runner = (_, _, _) => throw new OperationCanceledException();
        check((await service.ValidateConfigAsync()).Stage == "取消", "validation cancelled result");
        runner = (_, _, _) => throw new IOException("injected");
        check((await service.ValidateConfigAsync()).Details == "injected", "validation execution error detail");
        runner = (_, _, _) => Task.FromResult(new ConfigCheckOutput(4, "stdout", "stderr"));
        var failed = await service.ValidateConfigAsync();
        check(!failed.IsValid && failed.ExitCode == 4 && failed.Details == "stderr", "validation preserves exit and prefers stderr");
        check((await service.ValidateConfigAsync(configText: "{")).Stage == "JSON", "validation rejects malformed edits before execution");
        check((await service.ValidateConfigAsync(executablePath: Path.Combine(root,"missing.exe"))).Stage == "路径", "validation rejects missing executable");
        check((await service.ValidateConfigAsync(configPath: Path.Combine(root,"missing.json"))).Stage == "路径", "validation rejects missing config");
        check((await service.ValidateConfigAsync(cancellationToken: new CancellationToken(true))).Stage == "取消", "validation cancels file read");
        check(await ConfigCheckRunner.CaptureAsync(new StringReader(new string('a', 9000)), 1201, default) == new string('a', 1201), "process output drains while bounded");
        check(await ConfigCheckRunner.CaptureAsync(new StringReader("short"), 1201, default) == "short", "short output retained");
        service.PrepareForExit();
        await Reject<InvalidOperationException>(() => service.StartAsync(), "exit prevents queued restart");
        service.CancelExit();
        await service.SwitchModeAsync(ProxyMode.SystemProxy, 7890);
        check(service.PreferredMode == ProxyMode.SystemProxy, "cancelled exit permits preference changes again");

        var first = new ConnectionInfo { Id = "a", Source = "local", Destination = "target", UploadBytes = 1024, DownloadBytes = 1048576, StartTime = new DateTime(2026, 1, 1, 12, 13, 14) };
        var changed = new List<string?>(); first.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        first.Source = "new";
        check(changed.Contains(nameof(ConnectionInfo.SecondaryDisplay)) && first.SecondaryDisplay == "new", "source changes refresh secondary display");
        check(first.UploadDisplay == "1.0 KB" && first.DownloadDisplay == "1.0 MB" && first.StartTimeDisplay == "12:13:14", "connection boundary formatting");
        first.UploadBytes = 1023; first.DownloadBytes = 1073741824;
        check(first.UploadDisplay == "1023 B" && first.DownloadDisplay == "1.00 GB", "byte and gigabyte formatting");
        var second = new ConnectionInfo { Id = "b", Host = "Example", Network = "tcp", Source = "src", Destination = "dst", Rule = "direct", OutboundTag = "proxy", UploadBytes = 1, DownloadBytes = 2, StartTime = DateTime.MaxValue };
        check(first.UpdateFrom(second) && !first.UpdateFrom(second) && first.Id == "a" && first.OutboundTag == "proxy", "update copies all presentation fields and keeps identity");
        check(first.Network == second.Network && first.Source == second.Source && first.Destination == second.Destination
            && first.StartTime == second.StartTime && first.UploadBytes == second.UploadBytes && first.DownloadBytes == second.DownloadBytes
            && first.Host == second.Host && first.Rule == second.Rule, "update copies each connection field");
        check(first.Equals(new ConnectionInfo { Id = "a" }) && !first.Equals(second) && !new ConnectionInfo().Equals(null) && first.GetHashCode() == "a".GetHashCode(), "connection identity equality");
        check(first.PrimaryDisplay == "Example" && first.SecondaryDisplay == "src → dst" && first.ToClipboardText().Contains("direct"), "host presentation and clipboard");
        foreach (string field in new[] { "EXAMPLE", "TCP", "DIRECT", "SRC", "DST" })
            check(ConnectionPresentation.Select(new[] { second, new ConnectionInfo() }, field, "host").Single() == second, "search field " + field);
        check(!ConnectionPresentation.Select(new[] { second }, "absent", "host").Any(), "search excludes nonmatches");
        foreach (string sort in new[] { "host", "upload", "download", "time" })
            check(ConnectionPresentation.Select(new[] { first, second }, "", sort).SequenceEqual(new[] { first, second }), "equal sort keys preserve order " + sort);
        var different = new ConnectionInfo { Id="c", Host="Zulu", UploadBytes=99, DownloadBytes=0, StartTime=DateTime.MinValue };
        foreach (var row in new[] { (Sort:"host",First:second), (Sort:"upload",First:different), (Sort:"download",First:second), (Sort:"time",First:second) })
            check(ReferenceEquals(ConnectionPresentation.Select(new[]{different,second}," ",row.Sort).First(), row.First), "distinct sort keys " + row.Sort);
        var empty = new ConnectionInfo();
        check(empty.PrimaryDisplay == "" && empty.SecondaryDisplay == "" && empty.ToClipboardText().Contains("--"), "empty connection fallbacks");
        check(LogAnalysis.CleanAnsiSequences("\u001b[31merror\u001b[0m plain") == "error plain", "ANSI cleanup preserves text");
        check(LogAnalysis.Summarize(new[] { "DNS ERROR", "dns timeout" }).SequenceEqual(new[] { "dns: 2", "error: 1", "timeout: 1" }), "log frequency stable and case insensitive");
        check(LogAnalysis.Summarize(Array.Empty<string>()).Single() == "暂无高频关键词", "empty log analysis");
        var traffic = new TrafficData { UsedGB = 0, RemainingGB = 0, ResetTimestamp = 0 };
        check(traffic.HasTrafficData, "traffic accepts zero values");
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity }) { traffic.UsedGB = invalid; check(!traffic.HasTrafficData, "invalid used traffic " + invalid); }
        traffic.UsedGB = 1; traffic.RemainingGB = double.PositiveInfinity; check(!traffic.HasTrafficData, "nonfinite remaining traffic");
        traffic.RemainingGB = 1; traffic.ResetTimestamp = 253402300800; check(!traffic.HasTrafficData, "timestamp exceeds display range");
        traffic.ResetTimestamp = 253402300799; check(traffic.HasTrafficData, "maximum reset timestamp");
        traffic.Error = "failure"; check(!traffic.HasTrafficData, "traffic error prevents valid presentation");
        traffic.Error=null; traffic.UsedGB=null; check(!traffic.HasTrafficData,"missing used traffic");
        traffic.UsedGB=1;
        foreach(double? value in new double?[]{null,-1,double.NaN}) { traffic.RemainingGB=value; check(!traffic.HasTrafficData,"invalid remaining traffic " + value); }
        traffic.RemainingGB=1;
        foreach(long? value in new long?[]{null,-1}) { traffic.ResetTimestamp=value; check(!traffic.HasTrafficData,"invalid reset timestamp " + value); }
    }
}
