using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace singC.Models;

// Retain the complete document and patch only edited routing fields.
public sealed class RouteConfigDocument
{
    private readonly JsonObject _source;
    public IReadOnlyList<RouteRule> Rules { get; }
    public string FinalOutbound { get; }
    public bool AutoDetectInterface { get; }
    public string DefaultDomainResolver { get; }
    public IReadOnlyList<string> OutboundTags { get; }
    public IReadOnlyList<string> DnsTags { get; }
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default) { WriteIndented = true };
    public RouteConfigDocument(string text)
    {
        _source = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("配置必须是 JSON 对象。");
        var route = _source["route"] as JsonObject;
        if (_source["route"] != null && route == null) throw new InvalidDataException("route 必须是对象。");
        var rules = route?["rules"] as JsonArray;
        if (route?["rules"] != null && rules == null) throw new InvalidDataException("route.rules 必须是数组。");
        Rules = rules?.Select(node => new RouteRule(node as JsonObject ?? throw new InvalidDataException("路由规则必须是对象。"))).ToArray() ?? Array.Empty<RouteRule>();
        FinalOutbound = route?["final"]?.GetValue<string>() ?? "";
        AutoDetectInterface = route?["auto_detect_interface"]?.GetValue<bool>() ?? false;
        var resolver = route?["default_domain_resolver"];
        DefaultDomainResolver = resolver is JsonObject obj ? obj["server"]?.GetValue<string>() ?? "" : resolver?.GetValue<string>() ?? "";
        OutboundTags = ReadTags(_source["outbounds"]);
        DnsTags = ReadTags((_source["dns"] as JsonObject)?["servers"]);
    }
    private static string[] ReadTags(JsonNode? node) => node is JsonArray array
        ? array.OfType<JsonObject>().Select(o => o["tag"]?.ToString()).Where(t => !string.IsNullOrWhiteSpace(t)).Cast<string>().Distinct().ToArray()
        : Array.Empty<string>();
    public JsonObject Build(IEnumerable<RouteRule> rules, string final, bool autoDetect, string resolver)
    {
        var ruleArray = new JsonArray(rules.Select(rule => (JsonNode)rule.ToJson()).ToArray());
        var result = (JsonObject)_source.DeepClone();
        var route = result["route"] as JsonObject;
        if (route == null && ruleArray.Count == 0 && final == FinalOutbound && autoDetect == AutoDetectInterface && resolver == DefaultDomainResolver) return result;
        if (route == null) result["route"] = route = new JsonObject();
        if (ruleArray.Count > 0 || route.ContainsKey("rules")) route["rules"] = ruleArray;
        if (final != FinalOutbound) SetText(route, "final", final);
        if (autoDetect != AutoDetectInterface) route["auto_detect_interface"] = autoDetect;
        if (resolver != DefaultDomainResolver)
        {
            if (string.IsNullOrWhiteSpace(resolver)) route.Remove("default_domain_resolver");
            else if (route["default_domain_resolver"] is JsonObject options) options["server"] = resolver.Trim();
            else route["default_domain_resolver"] = resolver.Trim();
        }
        return result;
    }
    private static void SetText(JsonObject obj, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) obj.Remove(key);
        else obj[key] = value.Trim();
    }
}

