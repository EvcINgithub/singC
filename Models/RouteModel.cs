using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace singC.Models;

public sealed record MatchFieldOption(string Key, string Label, string Hint, bool IsBoolean = false, bool IsPort = false);

public sealed class RouteMatchCondition : INotifyPropertyChanged
{
    public static IReadOnlyList<MatchFieldOption> Fields { get; } = new[]
    {
        new MatchFieldOption("domain", "精确域名", "example.com，每行一个或用逗号分隔"),
        new MatchFieldOption("domain_suffix", "域名后缀", "example.com、cn，每行一个或用逗号分隔"),
        new MatchFieldOption("domain_keyword", "域名关键词", "关键词，每行一个或用逗号分隔"),
        new MatchFieldOption("domain_regex", "域名正则", "每行一个正则表达式"),
        new MatchFieldOption("ip_cidr", "目标 IP / CIDR", "1.1.1.1、10.0.0.0/8，每行一个或用逗号分隔"),
        new MatchFieldOption("source_ip_cidr", "来源 IP / CIDR", "192.168.1.0/24，每行一个或用逗号分隔"),
        new MatchFieldOption("port", "目标端口", "例如 80,443；范围请使用高级 JSON", IsPort: true),
        new MatchFieldOption("source_port", "来源端口", "例如 12345；范围请使用高级 JSON", IsPort: true),
        new MatchFieldOption("protocol", "协议", "例如 dns、http、tls、quic"),
        new MatchFieldOption("network", "网络", "tcp 或 udp"),
        new MatchFieldOption("rule_set", "规则集", "已定义的规则集标签，每行一个或用逗号分隔"),
        new MatchFieldOption("process_name", "进程名称", "例如 chrome.exe"),
        new MatchFieldOption("process_path", "进程路径", "完整进程路径，每行一个"),
        new MatchFieldOption("inbound", "入站标签", "已定义的入站标签，每行一个或用逗号分隔"),
        new MatchFieldOption("ip_is_private", "目标为私有 IP", "", IsBoolean: true),
        new MatchFieldOption("source_ip_is_private", "来源为私有 IP", "", IsBoolean: true)
    };
    public IReadOnlyList<MatchFieldOption> Options => Fields;
    private string _field;
    private string _value;
    private readonly string? _originalField;
    private readonly string? _originalValue;
    private readonly JsonNode? _originalNode;
    public RouteMatchCondition() { _field = "domain_suffix"; _value = ""; }
    private RouteMatchCondition(string field, JsonNode node, string value)
    {
        _field = _originalField = field;
        _value = _originalValue = value;
        _originalNode = node.DeepClone();
    }
    public string Field
    {
        get => _field;
        set
        {
            if (_field == value || !Fields.Any(f => f.Key == value)) return;
            _field = value;
            _value = IsBoolean ? "true" : "";
            Changed();
            Changed(nameof(Value)); Changed(nameof(IsBoolean)); Changed(nameof(BooleanValue)); Changed(nameof(Hint));
        }
    }
    public string Value { get => _value; set { if (_value == value) return; _value = value; Changed(); Changed(nameof(BooleanValue)); } }
    public bool IsBoolean => Fields.First(f => f.Key == Field).IsBoolean;
    public bool BooleanValue { get => _value == "true"; set => Value = value ? "true" : "false"; }
    public string Hint => Fields.First(f => f.Key == Field).Hint;
    public string Summary => Fields.First(f => f.Key == Field).Label + "：" + (string.IsNullOrWhiteSpace(Value) ? "待填写" : Value.Replace('\n', ' '));

    public static RouteMatchCondition? FromJson(string field, JsonNode? node)
    {
        var option = Fields.FirstOrDefault(f => f.Key == field);
        if (option == null || node == null) return null;
        if (option.IsBoolean)
            return node is JsonValue boolean && boolean.TryGetValue<bool>(out bool b) ? new(field, node, b ? "true" : "false") : null;
        var nodes = node is JsonArray array ? array.ToArray() : new[] { node };
        var values = new List<string>();
        foreach (var item in nodes)
        {
            if (item is not JsonValue value) return null;
            if (option.IsPort && value.TryGetValue<int>(out int port)) values.Add(port.ToString());
            else if (!option.IsPort && value.TryGetValue<string>(out string? text)) values.Add(text);
            else return null;
        }
        return new(field, node, string.Join("\n", values));
    }

    public JsonNode ToJson()
    {
        if (Field == _originalField && Value == _originalValue) return _originalNode!.DeepClone();
        if (IsBoolean) return JsonValue.Create(BooleanValue)!;
        string[] separators = Field is "domain_regex" or "process_path" ? new[] { "\r\n", "\n" } : new[] { "\r\n", "\n", ",", "，" };
        var values = Value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0) throw new InvalidDataException(Fields.First(f => f.Key == Field).Label + "不能为空；不需要的条件请删除。");
        var result = new JsonArray();
        foreach (var value in values)
        {
            if (Fields.First(f => f.Key == Field).IsPort)
            {
                if (!int.TryParse(value, out int port) || port is < 1 or > 65535) throw new InvalidDataException("端口必须是 1 到 65535 之间的整数，多个端口用逗号分隔。");
                result.Add(port);
            }
            else result.Add(value);
        }
        return result;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class RouteRule : INotifyPropertyChanged
{
    private readonly JsonObject _original;
    private readonly HashSet<string> _editableOriginalFields = new();
    private readonly HashSet<RouteMatchCondition> _subscribed = new();
    private string _action;
    private string _outbound;
    private bool _actionChanged, _outboundChanged, _isExpanded;
    private int _ordinal;
    public ObservableCollection<RouteMatchCondition> Conditions { get; } = new();
    public IReadOnlyList<string> ActionOptions { get; }
    public bool IsSimple => _original["type"]?.ToString() != "logical";
    public string AdvancedNote => IsSimple
        ? "表单未列出的原有字段会原样保留；更多选项可在高级 JSON 中编辑。"
        : "逻辑规则包含嵌套条件，请切换高级 JSON 编辑；可以在这里排序或删除整条规则。";
    public RouteRule() : this(new JsonObject { ["action"] = "route" }) { IsExpanded = true; }
    public RouteRule(JsonObject original)
    {
        _original = (JsonObject)original.DeepClone();
        _action = original["action"]?.ToString() ?? "route";
        _outbound = original["outbound"]?.ToString() ?? "";
        ActionOptions = new[] { "route", "reject", "sniff", "hijack-dns", "route-options", "resolve", "bypass", _action }.Distinct().ToArray();
        if (IsSimple)
            foreach (var property in original)
            {
                var condition = RouteMatchCondition.FromJson(property.Key, property.Value);
                if (condition == null) continue;
                Conditions.Add(condition);
                _editableOriginalFields.Add(property.Key);
            }
        Conditions.CollectionChanged += ConditionsChanged;
        SynchronizeSubscriptions();
    }
    public string Action { get => _action; set { if (_action == value) return; _action = value; _actionChanged = true; Changed(); Changed(nameof(Summary)); } }
    public string Outbound { get => _outbound; set { if (_outbound == value) return; _outbound = value; _outboundChanged = true; Changed(); Changed(nameof(Summary)); } }
    public int Ordinal { get => _ordinal; set { if (_ordinal == value) return; _ordinal = value; Changed(nameof(Title)); } }
    public string Title => "规则 " + Ordinal;
    public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; Changed(); } }
    private bool HasAdvancedFields => _original.Any(p => p.Key is not ("action" or "outbound") && !_editableOriginalFields.Contains(p.Key));
    public string Summary => (IsSimple ? Conditions.Count == 0
        ? HasAdvancedFields ? "高级条件/选项（见 JSON）" : "无匹配条件（匹配所有流量）"
        : string.Join("；", Conditions.Select(c => c.Summary)) : "逻辑规则")
        + " → " + Action + (string.IsNullOrWhiteSpace(Outbound) ? "" : " / " + Outbound);
    public void AddCondition()
    {
        var field = RouteMatchCondition.Fields.FirstOrDefault(f => Conditions.All(c => c.Field != f.Key));
        if (field == null) return;
        var condition = new RouteMatchCondition();
        if (Conditions.Any(c => c.Field == condition.Field)) condition.Field = field.Key;
        Conditions.Add(condition);
    }
    public JsonObject ToJson()
    {
        var result = (JsonObject)_original.DeepClone();
        if (_actionChanged) SetText(result, "action", Action);
        if (_outboundChanged) SetText(result, "outbound", Outbound);
        foreach (string key in _editableOriginalFields) result.Remove(key);
        var seen = new HashSet<string>();
        foreach (var condition in Conditions)
        {
            if (!seen.Add(condition.Field)) throw new InvalidDataException("同一种匹配类型只能添加一次；多个值请填写在同一个条件中。");
            result[condition.Field] = condition.ToJson();
        }
        return result;
    }
    private static void SetText(JsonObject obj, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) obj.Remove(key);
        else obj[key] = value.Trim();
    }
    private void ConditionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SynchronizeSubscriptions();
        Changed(nameof(Conditions)); Changed(nameof(Summary));
    }
    private void SynchronizeSubscriptions()
    {
        foreach (var removed in _subscribed.Except(Conditions).ToArray()) { removed.PropertyChanged -= ConditionChanged; _subscribed.Remove(removed); }
        foreach (var added in Conditions.Where(c => !_subscribed.Contains(c))) { added.PropertyChanged += ConditionChanged; _subscribed.Add(added); }
    }
    private void ConditionChanged(object? sender, PropertyChangedEventArgs e) { Changed(nameof(Conditions)); Changed(nameof(Summary)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
