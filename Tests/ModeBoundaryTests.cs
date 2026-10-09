using System.Text.Json.Nodes;
using singC.Models;

internal static class ModeBoundaryTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException) { check(true, name); return; }
            throw new Exception("Expected rejection: " + name);
        }
        foreach (int port in new[] { 0, 65536 }) Reject(() => ProxyModeConfig.Build("{}", ProxyMode.SystemProxy, port), "port outside range " + port);
        foreach (int port in new[] { 1, 65535 }) check(ProxyModeConfig.Build("{}", ProxyMode.SystemProxy, port).ProxyPort == port, "port boundary " + port);
        Reject(() => ProxyModeConfig.Build("{}", (ProxyMode)3), "undefined proxy mode");
        foreach (string json in new[] { "null", "[]", "{\"inbounds\":{}}", "{\"inbounds\":[null]}" })
            Reject(() => ProxyModeConfig.Build(json, ProxyMode.Configuration), "invalid inbound structure " + json);
        // Each row changes one independent prerequisite from the reusable-listener baseline.
        foreach (var row in new[] {
            (Type:"http",Users:"[]",Tls:"null",Host:"127.0.0.1",Reuse:true),
            (Type:"socks",Users:"[]",Tls:"null",Host:"127.0.0.1",Reuse:false),
            (Type:"http",Users:"[{}]",Tls:"null",Host:"127.0.0.1",Reuse:false),
            (Type:"http",Users:"[]",Tls:"{}",Host:"127.0.0.1",Reuse:false),
            (Type:"http",Users:"[]",Tls:"null",Host:"8.8.8.8",Reuse:false),
            (Type:"mixed",Users:"[]",Tls:"null",Host:"localhost",Reuse:true),
            (Type:"http",Users:"[]",Tls:"null",Host:"::1",Reuse:true),
            (Type:"http",Users:"[]",Tls:"null",Host:"invalid-host",Reuse:false) })
        {
            string json = $$"""{"inbounds":[{"type":"{{row.Type}}","users":{{row.Users}},"tls":{{row.Tls}},"listen":"{{row.Host}}","tag":"original"}]}""";
            var result = JsonNode.Parse(ProxyModeConfig.Build(json, ProxyMode.SystemProxy).Json)!;
            check(result["inbounds"]!.AsArray().Count == (row.Reuse ? 1 : 2), "listener reuse matrix " + row);
        }
        foreach (string host in new[] { "", "0.0.0.0", "::", "[::]", "localhost", "127.0.0.2" })
        {
            string json = $$"""{"inbounds":[{"type":"http","listen":"{{host}}","listen_port":1,"set_system_proxy":true}]}""";
            var result = ProxyModeConfig.Build(json, ProxyMode.Configuration);
            check(result.ProxyHost == (host is "" or "0.0.0.0" ? "127.0.0.1" : host is "::" or "[::]" ? "::1" : host), "managed proxy host " + host);
        }
        foreach (string port in new[] { "null", "0", "65536" })
            Reject(() => ProxyModeConfig.Build($$"""{"inbounds":[{"type":"http","listen_port":{{port}},"set_system_proxy":true}]}""", ProxyMode.Configuration), "managed proxy port " + port);
        Reject(() => ProxyModeConfig.Build("""{"inbounds":[{"type":"http","listen_port":1,"set_system_proxy":true},{"type":"mixed","listen_port":2,"set_system_proxy":true}]}""", ProxyMode.Configuration), "multiple managed proxies");
        var unique = JsonNode.Parse(ProxyModeConfig.Build("""{"inbounds":[{"type":"socks","tag":"singc-proxy"},{"type":"socks","tag":"singc-proxy-2"}]}""", ProxyMode.SystemProxy).Json)!;
        check(unique["inbounds"]![2]!["tag"]!.GetValue<string>() == "singc-proxy-3", "generated proxy tag avoids collisions");
        var tun = JsonNode.Parse(ProxyModeConfig.Build("""{"inbounds":[],"route":{"default_interface":"eth0"}}""", ProxyMode.Tun).Json)!;
        check(tun["route"]!["default_interface"]!.GetValue<string>() == "eth0" && tun["route"]!["auto_detect_interface"] == null, "TUN retains explicit interface");
    }
}
