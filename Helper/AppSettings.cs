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
        private static readonly string SettingsFilePath;
        private static Dictionary<string, string>? _settings;

        public const string NetworkTestUrlKey = "NetworkTestUrl";
        public const string NetworkTestHostKey = "NetworkTestHost";
        public const string NetworkTestPortKey = "NetworkTestPort";
        public const string NetworkTestExitIpUrlKey = "NetworkTestExitIpUrl";
        public const string NetworkTestHistoryKey = "NetworkTestHistory";
        public const string AutoReconnectKey = "AutoReconnect";
        public const string ConnectionRefreshIntervalKey = "ConnectionRefreshInterval";
        public const string ConnectionSortKey = "ConnectionSort";
        public const string LastPageKey = "LastPage";
        public const string StartWithWindowsKey = "StartWithWindows";
        public const string TrafficServiceUrlKey = "TrafficServiceUrl";
        public const string FileBrowserUrlKey = "FileBrowserUrl";
        public const string ChatUrlKey = "ChatUrl";

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

        static AppSettings()
        {
            // 存储到用户本地应用数据目录下的 settings.json
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(localAppData, "singC");
            Directory.CreateDirectory(appFolder);
            SettingsFilePath = Path.Combine(appFolder, "settings.json");
        }

        public static string? Get(string key)
        {
            LoadIfNeeded();
            _settings!.TryGetValue(key, out string? value);
            return value;
        }

        public static string? Get(PathKey key)
        {
            return Get(GetPathKey(key));
        }
        public static void Set(string key, string value)
        {
            LoadIfNeeded();
            _settings![key] = value;
            Save();
        }

        public static void Set(PathKey key, string value)
        {
            Set(GetPathKey(key), value);
        }
        public static void Init()
        {
            LoadIfNeeded();
        }

        public static List<string> GetList(string key)
        {
            LoadIfNeeded();
            if (!_settings!.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                return new List<string>();

            try
            {
                return JsonSerializer.Deserialize<List<string>>(value)?
                           .Where(item => !string.IsNullOrWhiteSpace(item))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .ToList()
                       ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void SetList(string key, IEnumerable<string> values)
        {
            LoadIfNeeded();
            var cleaned = values
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _settings![key] = JsonSerializer.Serialize(cleaned);
            Save();
        }
        private static void LoadIfNeeded()
        {
            if (_settings != null) return;

            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    _settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                               ?? new Dictionary<string, string>();
                }
                else
                {
                    _settings = new Dictionary<string, string>();
                }
            }
            catch
            {
                _settings = new Dictionary<string, string>();
            }
        }

        private static void Save()
        {
            if (_settings == null) return;
            string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }

        public static string GetPathKey(PathKey key)
        {
            return _paths[(int)key];
        }



    }
}
