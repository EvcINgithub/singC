using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using singC.Helpers;
using singC.Models;

internal static class ProxyModeTests
{
    private const string TunConfig = """
        {"log":{"level":"error"},"inbounds":[{"type":"tun","tag":"tun-in","address":["172.19.0.1/30"],"auto_route":true}],
        "outbounds":[{"type":"direct","tag":"direct"}],"route":{"auto_detect_interface":true,"rules":[{"inbound":["tun-in"],"outbound":"direct"}]}}
        """;
    private sealed class ProxyBackend : ISystemProxyBackend
    {
        public SystemProxySettings Current = new(13, "old-proxy:8080", "original-bypass", "https://pac.test/proxy.pac");
        public bool FailNextWrite;
        public SystemProxySettings Read() => Current;
        public void Write(SystemProxySettings settings)
        {
            Current = settings;
            if (FailNextWrite) { FailNextWrite = false; throw new IOException("Simulated partial proxy write"); }
        }
    }

    public static async Task<int> RunFixtureAsync(string[] args)
    {
        var config = JsonNode.Parse(await File.ReadAllTextAsync(args[2]))!;
        var inbounds = config["inbounds"]!.AsArray();
        bool mixed = inbounds.Any(i => i?["type"]?.GetValue<string>() == "mixed");
        if (args[0] == "check") return mixed && config["fixture_reject_mixed"]?.GetValue<bool>() == true ? 1 : 0;
        if (mixed && config["fixture_fail_mixed"]?.GetValue<bool>() == true)
        { Console.Error.WriteLine("Simulated mixed startup failure"); return 2; }
        TcpListener? listener = null;
        try
        {
            var proxy = inbounds.FirstOrDefault(i => i?["type"]?.GetValue<string>() is "mixed" or "http");
            if (proxy != null)
            {
                listener = new TcpListener(IPAddress.Loopback, proxy["listen_port"]!.GetValue<int>());
                listener.Start();
            }
            if (mixed && config["fixture_exit_ms"] != null)
                await Task.Delay(config["fixture_exit_ms"]!.GetValue<int>());
            else await Task.Delay(Timeout.Infinite);
            return 3;
        }
        catch (SocketException ex) { Console.Error.WriteLine(ex.Message); return 4; }
        finally { listener?.Stop(); }
    }

    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var proxy = ProxyModeConfig.Build(TunConfig, ProxyMode.SystemProxy, 18790);
        var converted = JsonNode.Parse(proxy.Json)!;
        check(!proxy.HasTun && proxy.ProxyPort == 18790 && proxy.ProxyHost == "127.0.0.1",
            "mode: proxy mode disables TUN and exposes a loopback listener");
        check(converted["inbounds"]![0]!["tag"]!.GetValue<string>() == "tun-in"
            && converted["route"]!["rules"]![0]!["inbound"]![0]!.GetValue<string>() == "tun-in",
            "mode: conversion retains inbound routing identity");
        check(JsonNode.Parse(TunConfig)!["inbounds"]![0]!["type"]!.GetValue<string>() == "tun",
            "mode: source configuration remains unchanged");
        var tun = ProxyModeConfig.Build(TunConfig, ProxyMode.Tun);
        check(tun.HasTun && tun.DisableSystemProxy && tun.ProxyPort == null, "mode: TUN requests temporary system proxy disable");
        var manual = ProxyModeConfig.Build(TunConfig, ProxyMode.Configuration);
        check(manual.HasTun && !manual.DisableSystemProxy && manual.ProxyPort == null, "mode: configuration mode preserves original behavior");
        var generatedTun = ProxyModeConfig.Build("""
            {"inbounds":[{"type":"mixed","tag":"local","listen":"127.0.0.1","listen_port":18080,"set_system_proxy":true}],
            "route":{"rules":[{"inbound":"local","outbound":"direct"}]},"dns":{"rules":[{"inbound":["local"],"server":"dns"}]}}
            """, ProxyMode.Tun);
        var generated = JsonNode.Parse(generatedTun.Json)!;
        check(generatedTun.HasTun && generated["route"]!["auto_detect_interface"]!.GetValue<bool>()
            && generated["route"]!["rules"]![0]!["action"]!.GetValue<string>() == "hijack-dns",
            "mode: missing TUN receives interface routing and DNS interception");
        check(generated["route"]!["rules"]![1]!["inbound"]!.AsArray().Count == 2
            && generated["dns"]!["rules"]![0]!["inbound"]!.AsArray().Count == 2,
            "mode: generated TUN is added to original proxy routing and DNS matches");
        check(!generated["inbounds"]![0]!["set_system_proxy"]!.GetValue<bool>(), "mode: core cannot race application proxy restoration");
        var sourceManaged = ProxyModeConfig.Build("""
            {"inbounds":[{"type":"mixed","listen":"0.0.0.0","listen_port":18080,"set_system_proxy":true}]}
            """, ProxyMode.Configuration);
        check(sourceManaged.ProxyHost == "127.0.0.1" && sourceManaged.ProxyPort == 18080,
            "mode: configuration-managed system proxy uses restorable application ownership");
        try { ProxyModeConfig.Build("{\"inbounds\":[{\"type\":\"tun\"},{\"type\":\"tun\"}]}", ProxyMode.SystemProxy); throw new Exception("Expected rejection"); }
        catch (InvalidDataException) { check(true, "mode: multiple TUN routing identities are not silently discarded"); }
        var freshProxy = JsonNode.Parse(ProxyModeConfig.Build("{\"outbounds\":[]}", ProxyMode.SystemProxy).Json)!;
        check(freshProxy["inbounds"]![0]!["type"]!.GetValue<string>() == "mixed", "mode: missing proxy listener is generated");
        var httpSource = "{\"inbounds\":[{\"type\":\"http\",\"tag\":\"http-in\",\"listen\":\"127.0.0.1\",\"listen_port\":18080}]}";
        var reused = JsonNode.Parse(ProxyModeConfig.Build(httpSource, ProxyMode.SystemProxy).Json)!;
        check(reused["inbounds"]!.AsArray().Count == 1 && reused["inbounds"]![0]!["type"]!.GetValue<string>() == "mixed",
            "mode: reused HTTP listener also serves SOCKS in system proxy mode");

        string journal = Path.Combine(root, "lease.json");
        var backend = new ProxyBackend();
        var original = backend.Current;
        var lease = new SystemProxyLease(backend, journal);
        lease.Apply("127.0.0.1", 7890);
        var applied = backend.Current;
        check(applied.Flags == 3 && applied.Server.Contains("https=127.0.0.1:7890"), "mode: Windows HTTP and HTTPS proxy activated");
        var competitor = new SystemProxyLease(backend, journal);
        competitor.Recover();
        check(backend.Current == applied, "mode: another instance cannot recover an active lease");
        try { competitor.Apply("127.0.0.1", 7891); throw new Exception("Expected lease contention"); }
        catch (IOException) { check(true, "mode: concurrent proxy ownership is rejected"); }
        lease.Restore();
        check(backend.Current == original, "mode: stop restores original proxy, PAC and autodetect flags");
        lease.Apply(null, null);
        check(backend.Current.Flags == 1, "mode: TUN disables manual and automatic system proxies");
        lease.Restore();
        check(backend.Current == original, "mode: leaving TUN restores all original settings");
        lease.Apply("127.0.0.1", 7890);
        backend.Current = original with { Server = "external-change:9000" };
        lease.Restore();
        check(backend.Current.Server == "external-change:9000", "mode: external proxy changes are not overwritten");
        backend.Current = applied;
        File.WriteAllText(journal, JsonSerializer.Serialize(new { Original = original, Applied = applied }));
        competitor.Recover();
        check(backend.Current == original && new FileInfo(journal).Length == 0, "mode: abandoned lease restores after application crash");
        backend.FailNextWrite = true;
        try { lease.Apply("127.0.0.1", 7890); throw new Exception("Expected write failure"); }
        catch (IOException) { check(backend.Current == original, "mode: partial system proxy write rolls back"); }

        if (!OperatingSystem.IsWindows()) return;
        // Read-only native smoke check; lifecycle tests below use the fake backend.
        var actualSettings = new WindowsSystemProxy().Read();
        check(actualSettings.Flags >= 0, "mode: Windows proxy snapshot interop works without changing settings");

        string fixtureExe = Environment.ProcessPath ?? throw new Exception("Missing test apphost");
        string source = Path.Combine(root, "config.json");
        File.WriteAllText(source, TunConfig);
        bool admin = true, failSave = false;
        int saves = 0;
        backend.Current = original;
        var service = new SingBoxService(() => fixtureExe, () => source,
            (_, _) => { if (failSave) throw new IOException("Simulated settings write failure"); saves++; },
            backend, Path.Combine(root, "runtime-test"), () => admin);
        int FreePort() { using var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return ((IPEndPoint)l.LocalEndpoint).Port; }
        async Task ExpectSwitchFailure(int port, string name)
        {
            try { await service.SwitchModeAsync(ProxyMode.SystemProxy, port); throw new Exception("Expected mode switch failure"); }
            catch (InvalidOperationException) { check(service.IsRunning && service.ActiveMode == ProxyMode.Configuration, name); }
        }
        try
        {
            await service.StartAsync();
            check(service.IsRunning && service.ActiveMode == ProxyMode.Configuration, "mode: process fixture starts in original mode");
            long firstGeneration = service.RunGeneration;
            File.WriteAllText(source, TunConfig.Replace("\"log\":", "\"fixture_reject_mixed\":true,\"log\":"));
            await ExpectSwitchFailure(FreePort(), "mode: invalid target is rejected while old process keeps running");
            check(service.RunGeneration == firstGeneration && saves == 0, "mode: validation failure preserves generation and preference");
            File.WriteAllText(source, TunConfig.Replace("\"log\":", "\"fixture_fail_mixed\":true,\"log\":"));
            await ExpectSwitchFailure(FreePort(), "mode: failed startup restarts the exact previous runtime configuration");
            check(backend.Current == original, "mode: failed switch restores Windows proxy state");
            File.WriteAllText(source, TunConfig);
            using (var occupied = new TcpListener(IPAddress.Loopback, 0))
            {
                occupied.Start();
                await ExpectSwitchFailure(((IPEndPoint)occupied.LocalEndpoint).Port, "mode: occupied proxy port triggers rollback");
            }
            failSave = true;
            await ExpectSwitchFailure(FreePort(), "mode: preference save failure rolls back the new process");
            failSave = false;
            await service.SwitchModeAsync(ProxyMode.SystemProxy, FreePort());
            check(service.IsRunning && service.ActiveMode == ProxyMode.SystemProxy && backend.Current.Flags == 3,
                "mode: live TUN-to-proxy switch activates only the ready proxy listener");
            admin = false;
            try { await service.SwitchModeAsync(ProxyMode.Tun, service.ProxyPort); throw new Exception("Expected permission rejection"); }
            catch (InvalidOperationException) { check(service.ActiveMode == ProxyMode.SystemProxy && service.IsRunning, "mode: missing TUN privileges do not stop current proxy"); }
            admin = true;
            await service.SwitchModeAsync(ProxyMode.Tun, service.ProxyPort);
            check(service.ActiveMode == ProxyMode.Tun && backend.Current.Flags == 1, "mode: live proxy-to-TUN switch clears the temporary proxy");
            await service.StopAsync();
            check(backend.Current == original && !service.IsRunning, "mode: stopping the core restores Windows settings");
            check(!Directory.EnumerateFiles(Path.Combine(root, "runtime-test", "runtime")).Any(), "mode: temporary runtime configurations are cleaned up");
            check(File.ReadAllText(source) == TunConfig, "mode: live switches never modify the user's source configuration");
            File.WriteAllText(source, TunConfig.Replace("\"log\":", "\"fixture_exit_ms\":2400,\"log\":"));
            await service.SwitchModeAsync(ProxyMode.SystemProxy, FreePort());
            await service.StartAsync();
            for (int i = 0; i < 60 && service.IsRunning; i++) await Task.Delay(100);
            check(service.State == SingBoxRuntimeState.Failed && backend.Current == original,
                "mode: unexpected core exit restores the system proxy");
        }
        finally { await service.StopAsync(); }

        string? realCore = Environment.GetEnvironmentVariable("SINGC_TEST_CORE_PATH");
        if (!string.IsNullOrWhiteSpace(realCore))
        {
            var coreCases = Enum.GetValues<ProxyMode>().Select(mode => (Name: mode.ToString(), Config: ProxyModeConfig.Build(TunConfig, mode)))
                .Append((Name: "GeneratedTun", Config: ProxyModeConfig.Build(proxy.Json, ProxyMode.Tun)));
            foreach (var coreCase in coreCases)
            {
                string configFile = Path.Combine(root, coreCase.Name + ".json");
                File.WriteAllText(configFile, coreCase.Config.Json);
                var start = new ProcessStartInfo(realCore) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                start.ArgumentList.Add("check"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(configFile);
                using var process = Process.Start(start)!;
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                check(process.ExitCode == 0, $"mode: installed sing-box validates {coreCase.Name} configuration {error}");
            }
        }
    }
}
