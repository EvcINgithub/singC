using System;
using System.IO;
using System.Text.Json;

namespace singC.Models;

// Do not use a record: generated ToString must not expose the secret.
public sealed class ClashApiEndpoint
{
    public Uri Address { get; }
    public string? Secret { get; }
    private ClashApiEndpoint(Uri address, string? secret) { Address = address; Secret = secret; }
    public static ClashApiEndpoint FromConfig(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("experimental", out var experimental)
            || !experimental.TryGetProperty("clash_api", out var api)
            || !api.TryGetProperty("external_controller", out var controller)
            || controller.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(controller.GetString()))
            throw new InvalidDataException("当前配置未启用 Clash API，无法采集连接与流量。");
        string address = controller.GetString()!;
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Host.Length == 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Clash API 监听地址无效。");
        var builder = new UriBuilder(uri);
        if (builder.Host == "0.0.0.0") builder.Host = "127.0.0.1";
        if (builder.Host is "::" or "[::]") builder.Host = "[::1]";
        string? secret = api.TryGetProperty("secret", out var value) ? value.GetString() : null;
        return new(builder.Uri, secret);
    }
    public override string ToString() => Address.ToString();
}
