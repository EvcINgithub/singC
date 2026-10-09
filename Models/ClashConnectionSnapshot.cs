using System;
using System.Collections.Generic;
using System.Text.Json;

namespace singC.Models;

internal sealed record ClashConnectionSnapshot(long? Upload, long? Download,
    List<ConnectionInfo>? Connections, IReadOnlyList<ConnectionTrafficSample>? Samples)
{
    public static ClashConnectionSnapshot Parse(ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, null);
        bool totals = Counter(root, "uploadTotal", out long upload) & Counter(root, "downloadTotal", out long download);
        if (!root.TryGetProperty("connections", out var array) || array.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            return new(totals ? upload : null, totals ? download : null, null, null);
        var connections = new List<ConnectionInfo>();
        var samples = new List<ConnectionTrafficSample>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string id = Text(item, "id");
                if (string.IsNullOrWhiteSpace(id) || !item.TryGetProperty("start", out var start)
                    || start.ValueKind != JsonValueKind.String || !start.TryGetDateTimeOffset(out var time)
                    || !Counter(item, "upload", out long up) || !Counter(item, "download", out long down)
                    || !ids.Add(id)) continue;
                string? tag = ReadOutboundTag(item);
                samples.Add(new(id, time, tag, up, down));
                item.TryGetProperty("metadata", out var metadata);
                connections.Add(new ConnectionInfo
                {
                    Id = id, StartTime = time.LocalDateTime, UploadBytes = up, DownloadBytes = down,
                    OutboundTag = tag, Rule = Text(item, "rule"), Network = Text(metadata, "network"),
                    Host = Text(metadata, "host"),
                    Source = Address(metadata, "sourceIP", "sourcePort"),
                    Destination = Address(metadata, "destinationIP", "destinationPort")
                });
            }
        return new(totals ? upload : null, totals ? download : null, connections, samples);
    }

    private static string Address(JsonElement obj, string ip, string port) => Text(obj, ip) + ":" + Text(obj, port);
    private static string Text(JsonElement obj, string key) => obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
        ? value.ToString() : "";
    private static bool Counter(JsonElement obj, string key, out long value)
    {
        value = 0;
        return obj.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value) && value >= 0;
    }
    internal static string? ReadOutboundTag(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("chains", out var chains)
            || chains.ValueKind != JsonValueKind.Array || chains.GetArrayLength() == 0) return null;
        var last = chains[chains.GetArrayLength() - 1];
        return last.ValueKind == JsonValueKind.String ? last.GetString() : null;
    }
}
