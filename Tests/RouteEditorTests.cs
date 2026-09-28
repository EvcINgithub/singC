using System.Text.Json.Nodes;
using singC.Models;

internal static class RouteEditorTests
{
    public static void Run(Action<bool, string> check)
    {
        const string source = """
        {
          "outbounds":[{"type":"direct","tag":"direct"},{"type":"selector","tag":"proxy","outbounds":["direct"]}],
          "dns":{"servers":[{"type":"local","tag":"local"}]},
          "route":{
            "final":"proxy",
            "default_domain_resolver":{"server":"local","strategy":"ipv4_only"},
            "rules":[
              {"action":"sniff"},
              {"domain_suffix":"example.com","protocol":["http","tls"],"port":[80,443],"outbound":"proxy","invert":true,"process_name":["browser.exe"],"custom":{"keep":1}},
              {"ip_is_private":true,"outbound":"direct"},
              {"type":"logical","mode":"or","rules":[{"domain":"a.example"},{"network":"udp"}],"action":"reject"}
            ]
          }
        }
        """;
        var doc = new RouteConfigDocument(source);
        var rules = doc.Rules.ToList();
        JsonObject Build() => doc.Build(rules, doc.FinalOutbound, doc.AutoDetectInterface, doc.DefaultDomainResolver);
        check(JsonNode.DeepEquals(JsonNode.Parse(source), Build()), "rules: opening and saving preserves every field, type and order");
        check(rules.Count == 4 && rules[0].Action == "sniff" && rules[2].Conditions.Single().IsBoolean,
            "rules: formerly hidden system rules remain visible and editable");
        check(!rules[3].IsSimple && rules[3].ToJson()["rules"] is JsonArray { Count: 2 }, "rules: nested logical rules survive unchanged");
        check(doc.OutboundTags.SequenceEqual(new[] { "direct", "proxy" }) && doc.DnsTags.SequenceEqual(new[] { "local" }),
            "rules: selectors use actual outbound and DNS tags");

        rules[1].Outbound = "direct";
        var edited = rules[1].ToJson();
        check(edited["outbound"]!.GetValue<string>() == "direct" && edited["custom"]!["keep"]!.GetValue<int>() == 1
            && edited["invert"]!.GetValue<bool>(), "rules: editing outbound preserves unknown routing fields");
        check(edited["port"] is JsonArray ports && ports[1]!.GetValue<int>() == 443, "rules: nonzero port arrays are retained");
        var suffix = rules[1].Conditions.Single(c => c.Field == "domain_suffix");
        suffix.Value = "example.org, example.net";
        check(rules[1].ToJson()["domain_suffix"] is JsonArray { Count: 2 }, "rules: comma-separated matching values serialize as arrays");
        rules[1].Conditions.Remove(suffix);
        check(!rules[1].ToJson().ContainsKey("domain_suffix") && rules[1].ToJson().ContainsKey("protocol"),
            "rules: removing one condition preserves other conditions");
        var port = rules[1].Conditions.Single(c => c.Field == "port");
        port.Value = "8443";
        check(rules[1].ToJson()["port"]![0]!.GetValue<int>() == 8443, "rules: edited port survives saving");
        port.Value = "0";
        Reject(() => rules[1].ToJson(), check, "rules: invalid port blocks serialization");
        port.Value = "80,65535";
        check(rules[1].ToJson()["port"] is JsonArray { Count: 2 }, "rules: multiple valid ports serialize");

        var fresh = new RouteRule { Outbound = "proxy" };
        fresh.AddCondition();
        check(fresh.Conditions.Count == 1 && fresh.Conditions[0].Field == "domain_suffix", "rules: new rule starts with one selected matching type");
        Reject(() => fresh.ToJson(), check, "rules: empty matching content cannot silently become a catch-all");
        fresh.Conditions[0].Value = "new.example";
        rules.Add(fresh);
        check(Build()["route"]!["rules"] is JsonArray { Count: 5 }, "rules: adding a rule updates serialized order");
        rules.RemoveAt(0);
        check(Build()["route"]!["rules"]![0]!["outbound"]!.GetValue<string>() == "direct", "rules: deleting a rule does not reinsert it as a system rule");
        rules.Remove(fresh); rules.Insert(0, fresh);
        check(Build()["route"]!["rules"]![0]!["domain_suffix"]![0]!.GetValue<string>() == "new.example", "rules: moving a rule preserves exact priority");
        fresh.AddCondition();
        fresh.Conditions[1].Field = "domain_suffix";
        fresh.Conditions[1].Value = "duplicate.example";
        Reject(() => fresh.ToJson(), check, "rules: duplicate condition types are rejected instead of overwriting");
        fresh.Conditions.RemoveAt(1);
        fresh.Conditions[0].Field = "ip_is_private";
        fresh.Conditions[0].BooleanValue = false;
        check(fresh.ToJson()["ip_is_private"]!.GetValue<bool>() == false && !fresh.ToJson().ContainsKey("domain_suffix"),
            "rules: changing matching type removes old field and retains explicit false");

        var resolver = doc.Build(doc.Rules, "direct", true, "another");
        check(resolver["route"]!["default_domain_resolver"]!["server"]!.GetValue<string>() == "another"
            && resolver["route"]!["default_domain_resolver"]!["strategy"]!.GetValue<string>() == "ipv4_only",
            "rules: changing resolver preserves resolver options");
        var empty = new RouteConfigDocument("{}");
        check(empty.Rules.Count == 0 && JsonNode.DeepEquals(JsonNode.Parse("{}"), empty.Build(empty.Rules, "", false, "")),
            "rules: absent route remains absent without edits");
        check(empty.Build(new[] { new RouteRule { Action = "sniff" } }, "", false, "")["route"]!["rules"] is JsonArray { Count: 1 },
            "rules: first rule creates a missing route section");
        var scalar = new RouteRule(JsonNode.Parse("""{"protocol":"dns","port":53,"action":"hijack-dns"}""")!.AsObject());
        check(scalar.ToJson()["port"]!.GetValue<int>() == 53 && scalar.ToJson()["protocol"]!.GetValue<string>() == "dns",
            "rules: scalar protocol and port survive round-trip");
        var regex = new RouteMatchCondition { Field = "domain_regex", Value = "^x{1,3}\\.example$" };
        check(regex.ToJson().AsArray().Count == 1, "rules: regex commas are not split into separate conditions");
        var originalRule = JsonNode.Parse("""{"domain":"old.example","outbound":"direct"}""")!.AsObject();
        var clonedRule = new RouteRule(originalRule) { Outbound = "proxy" };
        clonedRule.Conditions[0].Value = "new.example";
        clonedRule.ToJson();
        check(originalRule["outbound"]!.GetValue<string>() == "direct" && originalRule["domain"]!.GetValue<string>() == "old.example",
            "rules: editing and exporting does not mutate the original JSON nodes");
        check(new RouteRule(JsonNode.Parse("""{"port_range":["1000:2000"],"outbound":"direct"}""")!.AsObject()).Summary.Contains("高级条件"),
            "rules: unsupported matchers are not described as catch-all rules");
        Reject(() => new RouteConfigDocument("{broken"), check, "rules: invalid JSON is reported");
        Reject(() => new RouteConfigDocument("""{"route":{"rules":{}}}"""), check, "rules: malformed rules collection is reported");
    }

    private static void Reject(Action action, Action<bool,string> check, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { check(true, name); return; }
        throw new Exception("Expected rejection: " + name);
    }
}

