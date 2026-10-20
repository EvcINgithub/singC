using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace singC.Helpers;

public sealed record BandwagonCredentials(string Veid, string ApiKey)
{
    public override string ToString() => "BandwagonCredentials [redacted]";

    public static BandwagonCredentials Create(string? veid, string? apiKey)
    {
        veid = veid?.Trim() ?? "";
        apiKey = apiKey?.Trim() ?? "";
        if (veid.Length is 0 or > 32 || !veid.All(c => c is >= '0' and <= '9') || veid.All(c => c == '0'))
            throw new ArgumentException("VEID 必须为正整数。");
        if (apiKey.Length is 0 or > 4096 || apiKey.Any(char.IsControl))
            throw new ArgumentException("请填写有效的 API Key。");
        return new(veid, apiKey);
    }
}

// Both values commit together; a failed encryption/write never publishes a new pair.
public sealed class BandwagonSettings(SettingsStore store,
    Func<string, string> protect, Func<string, string> unprotect)
{
    public const string StorageKey = "BandwagonCredentials";
    public string? Version => store.Get(StorageKey);

    public BandwagonCredentials? Load()
    {
        var saved = Version;
        if (string.IsNullOrEmpty(saved)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<BandwagonCredentials>(unprotect(saved))
                ?? throw new JsonException();
            return BandwagonCredentials.Create(value.Veid, value.ApiKey);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            throw new IOException("搬瓦工凭据无法读取，请重新填写并保存。");
        }
    }

    public void Save(string veid, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(veid) && string.IsNullOrWhiteSpace(apiKey))
        {
            store.Set(StorageKey, "");
            return;
        }
        var value = BandwagonCredentials.Create(veid, apiKey);
        store.Set(StorageKey, protect(JsonSerializer.Serialize(value)));
    }
}

public static class WindowsSecretProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr value);

    public static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(Transform(bytes, true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Unprotect(string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        byte[] bytes = Transform(Convert.FromBase64String(value), false);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Transform(byte[] value, bool encrypt)
    {
        var pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
        var input = new Blob { Length = value.Length, Data = pinned.AddrOfPinnedObject() };
        Blob output = default;
        try
        {
            bool success = encrypt
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            pinned.Free();
            if (output.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }
}
