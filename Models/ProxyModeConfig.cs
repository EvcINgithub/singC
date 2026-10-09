using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace singC.Models;

public enum ProxyMode { Configuration, Tun, SystemProxy }

public sealed record PreparedModeConfig(string Json, bool HasTun, string? ProxyHost, int? ProxyPort, bool DisableSystemProxy);

public static class ProxyModeConfig
{
    public static string DisplayName(ProxyMode mode) => mode switch
    {
        ProxyMode.Tun => "TUN 模式",
        ProxyMode.SystemProxy => "系统代理模式",
        _ => "跟随配置"
    };

    // Always transform a fresh copy of the source; never write mode changes back to it.
    public static PreparedModeConfig Build(string json, ProxyMode mode, int port = 7890)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "代理端口必须在 1 到 65535 之间。");
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("配置必须是 JSON 对象。");
        var inbounds = root["inbounds"] as JsonArray;
        if (root["inbounds"] != null && inbounds == null) throw new InvalidDataException("inbounds 必须是数组。");
        if (inbounds == null) root["inbounds"] = inbounds = new JsonArray();
        if (inbounds.Any(i => i is not JsonObject)) throw new InvalidDataException("入站配置必须是 JSON 对象。");
        var tuns = inbounds.OfType<JsonObject>().Where(i => Type(i) == "tun").ToArray();
        string? proxyHost = null;
        int? proxyPort = null;

        if (mode == ProxyMode.SystemProxy)
        {
            ConfigureSystemProxy(inbounds, tuns, port);
            proxyHost = "127.0.0.1";
            proxyPort = port;
        }
        else if (mode == ProxyMode.Tun)
        {
            ConfigureTun(root, inbounds, tuns);
        }

        // singC owns Windows proxy restoration, including after a forced core exit.
        foreach (var inbound in inbounds.OfType<JsonObject>())
        {
            if (Type(inbound) is not ("http" or "mixed")) continue;
            if (mode == ProxyMode.Configuration && inbound["set_system_proxy"]?.GetValue<bool>() == true)
            {
                if (proxyPort != null) throw new InvalidDataException("多个入站同时设置系统代理，请仅保留一个 set_system_proxy。");
                proxyHost = inbound["listen"]?.GetValue<string>() ?? "127.0.0.1";
                if (proxyHost is "0.0.0.0" or "") proxyHost = "127.0.0.1";
                if (proxyHost is "::" or "[::]") proxyHost = "::1";
                proxyPort = inbound["listen_port"]?.GetValue<int>();
                if (proxyPort is not (>= 1 and <= 65535)) throw new InvalidDataException("系统代理入站缺少有效端口。");
            }
            inbound["set_system_proxy"] = false;
        }
        return new(root.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true }),
            inbounds.OfType<JsonObject>().Any(i => Type(i) == "tun"), proxyHost, proxyPort, mode == ProxyMode.Tun);
    }

    private static void ConfigureSystemProxy(JsonArray inbounds, JsonObject[] tuns, int port)
    {
            if (tuns.Length > 1) throw new InvalidDataException("配置包含多个 TUN 入站，请先合并为一个 TUN 入站后再快捷切换。");
            // Reuse the TUN tag so inbound-based routing and DNS rules still match.
            JsonObject? proxy = null;
            if (tuns.Length == 1)
            {
                proxy = new JsonObject { ["type"] = "mixed", ["tag"] = tuns[0]["tag"]?.GetValue<string>() ?? UniqueTag(inbounds, "singc-proxy") };
                inbounds[inbounds.IndexOf(tuns[0])] = proxy;
            }
            else
                proxy = inbounds.OfType<JsonObject>().FirstOrDefault(i => Type(i) is "http" or "mixed"
                    && (i["users"] is not JsonArray users || users.Count == 0) && i["tls"] == null
                    && IsLocalListen(i["listen"]?.GetValue<string>()));
            if (proxy == null)
            {
                proxy = new JsonObject { ["type"] = "mixed", ["tag"] = UniqueTag(inbounds, "singc-proxy") };
                inbounds.Add(proxy);
            }
            proxy["type"] = "mixed";
            proxy["listen"] = "127.0.0.1";
            proxy["listen_port"] = port;

    }

    private static void ConfigureTun(JsonObject root, JsonArray inbounds, JsonObject[] tuns)
    {
            if (tuns.Length == 0)
            {
                string tag = UniqueTag(inbounds, "singc-tun");
                var proxyTags = inbounds.OfType<JsonObject>().Where(i => Type(i) is "http" or "mixed" or "socks")
                    .Select(i => i["tag"]?.GetValue<string>()).Where(t => t != null).ToArray();
                inbounds.Add(new JsonObject
                {
                    ["type"] = "tun", ["tag"] = tag,
                    ["address"] = new JsonArray("172.19.0.1/30"),
                    ["auto_route"] = true, ["strict_route"] = true
                });
                var route = root["route"] as JsonObject;
                if (route == null) root["route"] = route = new JsonObject();
                if (route["default_interface"] == null) route["auto_detect_interface"] = true;
                var rules = route["rules"] as JsonArray;
                if (rules == null) route["rules"] = rules = new JsonArray();
                rules.Insert(0, new JsonObject { ["port"] = 53, ["action"] = "hijack-dns" });
                ExtendInboundRules(route, proxyTags, tag);
                ExtendInboundRules(root["dns"], proxyTags, tag);
            }
            else foreach (var tun in tuns) tun["auto_route"] = true;
    }

    private static string Type(JsonObject inbound) => inbound["type"]?.GetValue<string>() ?? "";
    private static bool IsLocalListen(string? host) => host == null || host == "localhost"
        || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    private static string UniqueTag(JsonArray inbounds, string prefix)
    {
        var tags = inbounds.OfType<JsonObject>().Select(i => i["tag"]?.GetValue<string>()).ToHashSet();
        string tag = prefix;
        for (int index = 2; tags.Contains(tag); index++) tag = prefix + "-" + index;
        return tag;
    }
    private static void ExtendInboundRules(JsonNode? node, string?[] oldTags, string tag)
    {
        if (node is JsonObject obj)
        {
            if (obj["inbound"] is JsonArray matches && matches.Any(m => oldTags.Contains(m?.GetValue<string>()))) matches.Add(tag);
            else if (obj["inbound"] is JsonValue value && oldTags.Contains(value.GetValue<string>()))
                obj["inbound"] = new JsonArray(value.GetValue<string>(), tag);
            foreach (var child in obj.ToArray()) if (child.Key != "inbound") ExtendInboundRules(child.Value, oldTags, tag);
        }
        else if (node is JsonArray array) foreach (var child in array) ExtendInboundRules(child, oldTags, tag);
    }
}
