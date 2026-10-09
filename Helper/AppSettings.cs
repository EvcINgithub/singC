// Helpers/AppSettings.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace singC.Helpers
{
    public static class AppSettings
    {
        private static readonly SettingsStore Store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "singC", "settings.json"));
        public static string StorageError => Store.ReadError;

        public const string NetworkTestUrlKey = "NetworkTestUrl";
        public const string NetworkTestHostKey = "NetworkTestHost";
        public const string NetworkTestPortKey = "NetworkTestPort";
        public const string NetworkTestExitIpUrlKey = "NetworkTestExitIpUrl";
        public const string NetworkTestHistoryKey = "NetworkTestHistory";
        public const string NetworkTestTimeoutKey = "NetworkTestTimeout";
        public const string NetworkTestCommonSitesKey = "NetworkTestCommonSites";
        public const string NetworkTestCheckExitIpKey = "NetworkTestCheckExitIp";
        public const string AutoReconnectKey = "AutoReconnect";
        public const string ConnectionRefreshIntervalKey = "ConnectionRefreshInterval";
        public const string ConnectionSortKey = "ConnectionSort";
        public const string LastPageKey = "LastPage";
        public const string StartWithWindowsKey = "StartWithWindows";
        public const string TrafficServiceUrlKey = "TrafficServiceUrl";
        public const string ProxyModeKey = "ProxyMode";

        public static bool IsServiceUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

        public enum PathKey{
            SingBoxPathKey,
            ConfigPathKey,
            BackgroundImagePathKey
        }

        private static List<String> _paths = new List<String>([
            "SingBoxPath",
            "ConfigPath",
            "BackgroundImagePath"]);

        public static string? Get(string key) => Store.Get(key);
        public static string? Get(PathKey key) => Get(GetPathKey(key));
        public static void Set(string key, string value) => Store.Set(key, value);
        public static void Set(PathKey key, string value) => Set(GetPathKey(key), value);
        public static void Init() { _ = Store.ReadError; }
        public static List<string> GetList(string key) => Store.GetList(key);
        public static void SetList(string key, IEnumerable<string> values) => Store.SetList(key, values);
        public static string GetPathKey(PathKey key)
        {
            return _paths[(int)key];
        }



    }
}
