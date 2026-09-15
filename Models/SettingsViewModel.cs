using Microsoft.UI.Xaml;
using singC.Helpers;
using singC.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Input;
using WinRT.Interop;

namespace singC.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged
    {
        private readonly Window _window;
        private const string ConfigPathsKey = "ConfigPaths";
        private bool _isChangingConfigSelection;
        public ObservableCollection<RouteRule> RouteRules { get; } = new();
        public ObservableCollection<string> ConfigPaths { get; } = new();
        public ObservableCollection<ConfigBackupInfo> ConfigBackups { get; } = new();
        private string _finalOutbound = "remote";
        private bool _isLoadingConfig;
        private bool _hasUnsavedChanges;

        private static readonly JsonSerializerOptions IndentedOptions = new()
        {
            WriteIndented = true,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        };
        public string FinalOutbound
        {
            get => _finalOutbound;
            set
            {
                if (_finalOutbound == value) return;
                _finalOutbound = value;
                OnPropertyChanged();
                MarkConfigDirty();
                UpdateRouteJson();
            }
        }

        private bool _autoDetectInterface = true;
        public bool AutoDetectInterface
        {
            get => _autoDetectInterface;
            set
            {
                if (_autoDetectInterface == value) return;
                _autoDetectInterface = value;
                OnPropertyChanged();
                MarkConfigDirty();
                UpdateRouteJson();
            }
        }

        private string _defaultDomainResolver = "local";
        public string DefaultDomainResolver
        {
            get => _defaultDomainResolver;
            set
            {
                if (_defaultDomainResolver == value) return;
                _defaultDomainResolver = value;
                OnPropertyChanged();
                MarkConfigDirty();
                UpdateRouteJson();
            }
        }

        // ========== 原有路径属性 ==========
        private string _singBoxPath = string.Empty;
        public string SingBoxPath
        {
            get => _singBoxPath;
            set
            {
                _singBoxPath = value;
                OnPropertyChanged();
                AppSettings.Set(AppSettings.PathKey.SingBoxPathKey, value);
            }
        }

        private string _configPath = string.Empty;
        public string ConfigPath
        {
            get => _configPath;
            set
            {
                if (_configPath == value) return;

                _configPath = value;
                OnPropertyChanged();
                AppSettings.Set(AppSettings.PathKey.ConfigPathKey, value);
                AddConfigPath(value, select: false);
                RefreshBackups();

                if (SelectedConfigPath != value)
                    SelectedConfigPath = value;
            }
        }

        private string _selectedConfigPath = string.Empty;
        public string SelectedConfigPath
        {
            get => _selectedConfigPath;
            set
            {
                if (_selectedConfigPath == value) return;

                _selectedConfigPath = value;
                OnPropertyChanged();

                if (!_isChangingConfigSelection && !string.IsNullOrWhiteSpace(value))
                {
                    _isChangingConfigSelection = true;
                    ConfigPath = value;
                    _isChangingConfigSelection = false;
                    _ = LoadConfigAsync();
                }
            }
        }

        private string _backgroundImagePath = string.Empty;
        private bool _startWithWindows;
        public string BackgroundImagePath
        {
            get => _backgroundImagePath;
            set
            {
                _backgroundImagePath = value;
                OnPropertyChanged();
                AppSettings.Set(AppSettings.PathKey.BackgroundImagePathKey, value);
            }
        }

        private string _configText = string.Empty;
        public string ConfigText
        {
            get => _configText;
            set
            {
                if (_configText == value) return;
                _configText = value;
                OnPropertyChanged();
                MarkConfigDirty();
            }
        }

        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (_startWithWindows == value) return;
                try
                {
                    StartupManager.SetEnabled(value);
                    _startWithWindows = value;
                    OnPropertyChanged();
                    AppSettings.Set(AppSettings.StartWithWindowsKey, value.ToString());
                }
                catch (Exception ex)
                {
                    StatusMessage = $"设置开机启动失败：{ex.Message}";
                    OnPropertyChanged();
                }
            }
        }

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set
            {
                if (_hasUnsavedChanges == value) return;
                _hasUnsavedChanges = value;
                OnPropertyChanged();
            }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }
        private static bool IsSystemRule(JsonObject ruleObj)
        {
            // 空对象不参与判断
            if (ruleObj == null) return false;

            // 规则1: { "action": "sniff" }   (仅此一个字段)
            if (ruleObj.Count == 1 && ruleObj["action"]?.GetValue<string>() == "sniff")
                return true;

            // 规则2: { "protocol": "dns", "action": "hijack-dns" } 
            if (ruleObj.Count == 2 &&
                ruleObj["protocol"]?.GetValue<string>() == "dns" &&
                ruleObj["action"]?.GetValue<string>() == "hijack-dns")
                return true;

            // 规则3: { "network": "udp", "port": 443, "action": "reject" }
            if (ruleObj.Count == 3 &&
                ruleObj["network"]?.GetValue<string>() == "udp" &&
                ruleObj["port"]?.GetValue<int>() == 443 &&
                ruleObj["action"]?.GetValue<string>() == "reject")
                return true;

            // 规则4: { "ip_is_private": true, "outbound": "direct" }
            if (ruleObj.Count == 2 &&
                ruleObj["ip_is_private"]?.GetValue<bool>() == true &&
                ruleObj["outbound"]?.GetValue<string>() == "direct")
                return true;

            return false;
        }

        // ========== 模式切换属性 ==========
        private bool _isAdvancedMode;
        public bool IsAdvancedMode
        {
            get => _isAdvancedMode;
            set
            {
                _isAdvancedMode = value;
                OnPropertyChanged();
                if (value) // 切换到高级模式：把表单内容写回 JSON 文本
                    ConfigText = ConfigJsonObject?.ToJsonString(IndentedOptions) ?? "{}";
                else        // 切换到简易模式：重新解析 JSON 到表单
                    ParseConfigToForm();
            }
        }        
        // 解析后的 JSON 对象（后台数据源）
        private JsonObject? ConfigJsonObject { get; set; }

        // ========== 命令 ==========
        public ICommand BrowseSingBoxCommand { get; }
        public ICommand BrowseConfigCommand { get; }
        public ICommand RemoveConfigCommand { get; }
        public ICommand BrowseBackgroundCommand { get; }
        public ICommand ClearBackgroundCommand { get; }
        public ICommand LoadConfigCommand { get; }
        public ICommand SaveConfigCommand { get; }
        public ICommand CheckConfigCommand { get; }
        public ICommand RestoreBackupCommand { get; }
        public ICommand DeleteBackupCommand { get; }
        public ICommand ResetConfigCommand { get; }
        public ICommand ApplySimpleSettingsCommand { get; }
        public ICommand ToggleModeCommand { get; }
        public ICommand AddRuleCommand { get; }
        public ICommand RemoveRuleCommand { get; }
        public ICommand MoveRuleUpCommand { get; }
        public ICommand MoveRuleDownCommand { get; }
        // ========== 构造函数 ==========
        public SettingsViewModel(Window window)
        {
            _window = window;
            RouteRules.CollectionChanged += RouteRules_CollectionChanged;

            // 从 AppSettings 恢复路径
            _singBoxPath = AppSettings.Get(AppSettings.PathKey.SingBoxPathKey) ?? string.Empty;
            _configPath = AppSettings.Get(AppSettings.PathKey.ConfigPathKey) ?? string.Empty;
            foreach (var path in AppSettings.GetList(ConfigPathsKey))
                ConfigPaths.Add(path);
            AddConfigPath(_configPath, select: false);
            _selectedConfigPath = _configPath;
            _backgroundImagePath = AppSettings.Get(AppSettings.PathKey.BackgroundImagePathKey) ?? string.Empty;
            _startWithWindows = StartupManager.IsEnabled();

            // 初始化所有命令
            BrowseSingBoxCommand = new AsyncRelayCommand(BrowseSingBoxAsync);
            BrowseConfigCommand = new AsyncRelayCommand(BrowseConfigAsync);
            RemoveConfigCommand = new RelayCommand(RemoveSelectedConfig);
            BrowseBackgroundCommand = new AsyncRelayCommand(BrowseBackgroundAsync);
            ClearBackgroundCommand = new RelayCommand(() => BackgroundImagePath = string.Empty);
            LoadConfigCommand = new AsyncRelayCommand(LoadConfigAsync);
            SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
            CheckConfigCommand = new AsyncRelayCommand(CheckConfigAsync);
            RestoreBackupCommand = new RelayCommand<ConfigBackupInfo>(backup => _ = RestoreBackupAsync(backup));
            DeleteBackupCommand = new RelayCommand<ConfigBackupInfo>(DeleteBackup);
            ResetConfigCommand = new AsyncRelayCommand(LoadConfigAsync);
            ApplySimpleSettingsCommand = new RelayCommand(ApplySimpleSettings);
            ToggleModeCommand = new RelayCommand(() => IsAdvancedMode = !IsAdvancedMode);
            AddRuleCommand = new RelayCommand(AddRule);
            RemoveRuleCommand = new RelayCommand<RouteRule>(RemoveRule);
            MoveRuleUpCommand = new RelayCommand<RouteRule>(MoveRuleUp);
            MoveRuleDownCommand = new RelayCommand<RouteRule>(MoveRuleDown);
            // 如果有有效配置文件路径，自动加载
            if (!string.IsNullOrEmpty(ConfigPath) && File.Exists(ConfigPath))
                _ = LoadConfigAsync();
            RefreshBackups();
        }

        // ========== 文件浏览方法 ==========
        private async Task BrowseSingBoxAsync()
        {
            var path = await PickFileAsync("可执行文件 (*.exe)|*.exe");
            if (path != null) SingBoxPath = path;
        }

        private async Task BrowseConfigAsync()
        {
            var path = await PickFileAsync("JSON 配置文件 (*.json)|*.json");
            if (path != null)
            {
                ConfigPath = path;
                AddConfigPath(path, select: true);
                await LoadConfigAsync();
            }
        }

        private void AddConfigPath(string path, bool select)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!File.Exists(path)) return;

            if (!ConfigPaths.Any(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigPaths.Add(path);
                SaveConfigPaths();
            }

            if (select && SelectedConfigPath != path)
                SelectedConfigPath = path;
        }

        private void RemoveSelectedConfig()
        {
            if (string.IsNullOrWhiteSpace(SelectedConfigPath)) return;

            var removedPath = SelectedConfigPath;
            ConfigPaths.Remove(removedPath);
            SaveConfigPaths();

            var nextPath = ConfigPaths.FirstOrDefault() ?? string.Empty;
            _isChangingConfigSelection = true;
            _selectedConfigPath = nextPath;
            OnPropertyChanged(nameof(SelectedConfigPath));
            ConfigPath = nextPath;
            _isChangingConfigSelection = false;

            if (!string.IsNullOrWhiteSpace(nextPath) && File.Exists(nextPath))
                _ = LoadConfigAsync();
            else
            {
                ConfigText = string.Empty;
                StatusMessage = "已移除配置。";
            }
        }

        private void SaveConfigPaths()
        {
            AppSettings.SetList(ConfigPathsKey, ConfigPaths);
        }

        private async Task BrowseBackgroundAsync()
        {
            var path = await PickFileAsync("图片文件|*.jpg;*.jpeg;*.png;*.bmp");
            if (path != null) BackgroundImagePath = path;
        }

        private Task<string?> PickFileAsync(string filter)
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            try
            {
                string? path = NativeFileDialog.ShowOpenFileDialog(hwnd, filter);
                return Task.FromResult(path);
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }

        // ========== 配置加载/保存（已整合简易模式） ==========
        public async Task LoadConfigAsync()
        {
            if (string.IsNullOrEmpty(ConfigPath) || !File.Exists(ConfigPath))
            {
                StatusMessage = "配置文件路径无效，请先选择文件。";
                return;
            }

            try
            {
                _isLoadingConfig = true;
                ConfigText = await File.ReadAllTextAsync(ConfigPath);
                // 加载后自动解析到简易表单
                ParseConfigToForm();
                HasUnsavedChanges = false;
                StatusMessage = SingBoxService.Instance.IsRunning
                    ? "配置文件已加载，当前 sing-box 正在运行，重启后生效。"
                    : "配置文件已加载。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"读取失败：{ex.Message}";
            }
            finally
            {
                _isLoadingConfig = false;
            }
        }

        public async Task CheckConfigAsync()
        {
            string configText = GetConfigTextForSave();
            var result = await SingBoxService.Instance.ValidateConfigAsync(ConfigPath, configText);
            StatusMessage = result.ToUserMessage();
        }

        public async Task SaveConfigAsync()
        {
            if (string.IsNullOrEmpty(ConfigPath))
            {
                StatusMessage = "请先选择配置文件路径。";
                return;
            }

            if (SingBoxService.Instance.IsRunning)
            {
                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                {
                    Title = "Sing-box 正在运行",
                    Content = "当前 sing-box 正在运行。保存后需要重启才能加载新配置。\n\n确定要保存吗？",
                    PrimaryButtonText = "保存",
                    CloseButtonText = "取消",
                    XamlRoot = (_window.Content as FrameworkElement)?.XamlRoot
                };
                if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                {
                    StatusMessage = "保存已取消。";
                    return;
                }
            }

            string configText = GetConfigTextForSave();
            var validation = await SingBoxService.Instance.ValidateConfigAsync(ConfigPath, configText);
            if (!validation.IsValid)
            {
                StatusMessage = validation.ToUserMessage();
                return;
            }

            try
            {
                CreateConfigBackup();
                string tempPath = Path.Combine(
                    Path.GetDirectoryName(ConfigPath) ?? AppContext.BaseDirectory,
                    $".{Path.GetFileName(ConfigPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllTextAsync(tempPath, configText, new System.Text.UTF8Encoding(false));
                    if (File.Exists(ConfigPath))
                        File.Replace(tempPath, ConfigPath, null, ignoreMetadataErrors: true);
                    else
                        File.Move(tempPath, ConfigPath);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }

                ConfigText = configText;
                HasUnsavedChanges = false;
                RefreshBackups();
                StatusMessage = "配置保存成功，已生成备份。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"保存失败：{ex.Message}";
            }
        }

        private string GetConfigTextForSave()
        {
            if (!IsAdvancedMode && ConfigJsonObject != null)
            {
                UpdateRouteJson();
                return ConfigJsonObject.ToJsonString(IndentedOptions);
            }

            return ConfigText;
        }

        private void CreateConfigBackup()
        {
            if (!File.Exists(ConfigPath)) return;

            string legacyBackup = ConfigPath + ".bak";
            File.Copy(ConfigPath, legacyBackup, overwrite: true);

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string backupPath = $"{ConfigPath}.bak-{timestamp}";
            File.Copy(ConfigPath, backupPath, overwrite: false);
            RefreshBackups();

            foreach (var oldBackup in ConfigBackups.Skip(10).ToList())
            {
                try { File.Delete(oldBackup.FilePath); } catch { }
            }
            RefreshBackups();
        }

        private void RefreshBackups()
        {
            ConfigBackups.Clear();
            if (string.IsNullOrWhiteSpace(ConfigPath)) return;

            string directory = Path.GetDirectoryName(ConfigPath) ?? string.Empty;
            string pattern = $"{Path.GetFileName(ConfigPath)}.bak-*";
            if (!Directory.Exists(directory)) return;

            foreach (string path in Directory.EnumerateFiles(directory, pattern)
                         .OrderByDescending(File.GetLastWriteTime)
                         .Take(10))
            {
                ConfigBackups.Add(new ConfigBackupInfo(path));
            }
        }

        private async Task RestoreBackupAsync(ConfigBackupInfo? backup)
        {
            if (backup == null || !File.Exists(backup.FilePath) || string.IsNullOrWhiteSpace(ConfigPath)) return;

            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "恢复配置",
                Content = $"将使用备份覆盖当前配置：\n{backup.FileName}\n\n当前文件会先备份。是否继续？",
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                XamlRoot = (_window.Content as FrameworkElement)?.XamlRoot
            };
            if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

            try
            {
                CreateConfigBackup();
                string restoredText = await File.ReadAllTextAsync(backup.FilePath);
                var validation = await SingBoxService.Instance.ValidateConfigAsync(ConfigPath, restoredText);
                if (!validation.IsValid)
                {
                    StatusMessage = $"恢复失败，备份校验未通过：{validation.ToUserMessage()}";
                    return;
                }

                string tempPath = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(tempPath, restoredText, new System.Text.UTF8Encoding(false));
                    File.Replace(tempPath, ConfigPath, null, ignoreMetadataErrors: true);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }

                await LoadConfigAsync();
                StatusMessage = "配置已从备份恢复。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"恢复失败：{ex.Message}";
            }
        }

        private void DeleteBackup(ConfigBackupInfo? backup)
        {
            if (backup == null) return;
            try
            {
                File.Delete(backup.FilePath);
                RefreshBackups();
                StatusMessage = "备份已删除。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"删除备份失败：{ex.Message}";
            }
        }

        // ========== 简易模式核心方法 ==========
        private void AddRule()
        {
            RouteRules.Add(new RouteRule());
            MarkConfigDirty();
            UpdateRouteJson();
        }

        private void RemoveRule(RouteRule? rule)
        {
            if (rule != null)
                RouteRules.Remove(rule);
            MarkConfigDirty();
            UpdateRouteJson();
        }

        private void MoveRuleUp(RouteRule? rule)
        {
            if (rule == null) return;
            int index = RouteRules.IndexOf(rule);
            if (index > 0)
            {
                RouteRules.Move(index, index - 1);
                MarkConfigDirty();
                UpdateRouteJson();
            }
        }

        private void MoveRuleDown(RouteRule? rule)
        {
            if (rule == null) return;
            int index = RouteRules.IndexOf(rule);
            if (index < RouteRules.Count - 1)
            {
                RouteRules.Move(index, index + 1);
                MarkConfigDirty();
                UpdateRouteJson();
            }
        }

        private void RouteRules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (RouteRule rule in e.OldItems)
                    rule.PropertyChanged -= RouteRule_PropertyChanged;
            }

            if (e.NewItems != null)
            {
                foreach (RouteRule rule in e.NewItems)
                    rule.PropertyChanged += RouteRule_PropertyChanged;
            }

            MarkConfigDirty();
        }

        private void RouteRule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            MarkConfigDirty();
        }

        private void MarkConfigDirty()
        {
            if (!_isLoadingConfig)
                HasUnsavedChanges = true;
        }
        private void ParseConfigToForm()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ConfigText)) return;
                ConfigJsonObject = JsonNode.Parse(ConfigText)?.AsObject();
                if (ConfigJsonObject == null) return;

                if (ConfigJsonObject != null)
                {
                    ParseRouteToForm(); // 替代之前的端口/DNS 解析
                }
            }
            catch
            {
                // 解析失败保持表单不变
            }
        }

        private void ApplySimpleSettings()
        {
            _ = SaveConfigAsync();
        }

        // ========== INotifyPropertyChanged ==========
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void ParseRouteToForm()
        {
            if (ConfigJsonObject == null) return;
            var routeObj = ConfigJsonObject["route"]?.AsObject();
            if (routeObj == null) return;

            RouteRules.Clear();

            var rulesArray = routeObj["rules"]?.AsArray();
            if (rulesArray != null)
            {
                foreach (var ruleNode in rulesArray)
                {
                    var ruleObj = ruleNode?.AsObject();
                    if (ruleObj == null) continue;

                    // 跳过系统保留规则
                    if (IsSystemRule(ruleObj)) continue;

                    var rule = new RouteRule
                    {
                        Action = ruleObj["action"]?.GetValue<string>() ?? "",
                        Protocol = ruleObj["protocol"]?.GetValue<string>() ?? "",
                        Network = ruleObj["network"]?.GetValue<string>() ?? "",
                        Port = (int?)ruleObj["port"]?.GetValue<int>(),
                        IpIsPrivate = ruleObj["ip_is_private"]?.GetValue<bool>() ?? false,
                        Outbound = ruleObj["outbound"]?.GetValue<string>() ?? "",
                        RuleSet = ReadStringArray(ruleObj["rule_set"]),
                        DomainSuffix = ReadStringArray(ruleObj["domain_suffix"]),
                        IpCidr = ReadStringArray(ruleObj["ip_cidr"]),
                        Domain = ReadStringArray(ruleObj["domain"])
                    };
                    RouteRules.Add(rule);
                }
            }

            // 全局选项依然读取
            FinalOutbound = routeObj["final"]?.GetValue<string>() ?? "remote";
            AutoDetectInterface = routeObj["auto_detect_interface"]?.GetValue<bool>() ?? true;
            DefaultDomainResolver = routeObj["default_domain_resolver"]?.GetValue<string>() ?? "local";
        }

        private static string ReadStringArray(JsonNode? node)
        {
            var array = node?.AsArray();
            if (array == null) return string.Empty;

            return string.Join(",", array.Select(item => item?.GetValue<string>() ?? string.Empty));
        }

        private void UpdateRouteJson()
        {
            if (ConfigJsonObject == null) return;

            var routeObj = ConfigJsonObject["route"]?.AsObject() ?? new JsonObject();
            ConfigJsonObject["route"] = routeObj;

            var newRulesArray = new JsonArray();

            // 1. 保留原来的系统规则（从原始 route.rules 中提取）
            var originalRules = routeObj["rules"]?.AsArray();
            if (originalRules != null)
            {
                foreach (var ruleNode in originalRules)
                {
                    var ruleObj = ruleNode?.AsObject();
                    if (ruleObj != null && IsSystemRule(ruleObj))
                    {
                        // 直接复制原节点（深度克隆以保持独立）
                        newRulesArray.Add(JsonNode.Parse(ruleObj.ToJsonString()));
                    }
                }
            }

            // 2. 追加用户自定义规则
            foreach (var rule in RouteRules)
            {
                var ruleObj = new JsonObject();
                if (!string.IsNullOrWhiteSpace(rule.Action)) ruleObj["action"] = rule.Action;
                if (!string.IsNullOrWhiteSpace(rule.Protocol)) ruleObj["protocol"] = rule.Protocol;
                if (!string.IsNullOrWhiteSpace(rule.Network)) ruleObj["network"] = rule.Network;
                if (rule.Port.HasValue && rule.Port.Value == 0) ruleObj["port"] = rule.Port.Value;
                if (rule.IpIsPrivate) ruleObj["ip_is_private"] = true;
                if (!string.IsNullOrWhiteSpace(rule.Outbound)) ruleObj["outbound"] = rule.Outbound;
                // ip_cidr
                if (!string.IsNullOrWhiteSpace(rule.IpCidr))
                {
                    var ips = rule.IpCidr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(s => s.Trim())
                                         .Where(s => !string.IsNullOrEmpty(s));
                    var ipArray = new JsonArray();
                    foreach (var ip in ips) ipArray.Add(ip);
                    ruleObj["ip_cidr"] = ipArray;
                }

                // domain (精确域名)
                if (!string.IsNullOrWhiteSpace(rule.Domain))
                {
                    var domains = rule.Domain.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(s => s.Trim())
                                             .Where(s => !string.IsNullOrEmpty(s));
                    var domainArray = new JsonArray();
                    foreach (var d in domains) domainArray.Add(d);
                    ruleObj["domain"] = domainArray;
                }
                // rule_set 序列化
                if (!string.IsNullOrWhiteSpace(rule.RuleSet))
                {
                    var sets = rule.RuleSet.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim())
                                           .Where(s => !string.IsNullOrEmpty(s));
                    var setArray = new JsonArray();
                    foreach (var s in sets) setArray.Add(s);
                    ruleObj["rule_set"] = setArray;
                }

                // domain_suffix 序列化
                if (!string.IsNullOrWhiteSpace(rule.DomainSuffix))
                {
                    var domains = rule.DomainSuffix.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(d => d.Trim())
                                                   .Where(d => !string.IsNullOrEmpty(d));
                    var domainArray = new JsonArray();
                    foreach (var d in domains) domainArray.Add(d);
                    ruleObj["domain_suffix"] = domainArray;
                }

                newRulesArray.Add(ruleObj);
            }

            routeObj["rules"] = newRulesArray;

            // 全局选项
            routeObj["final"] = FinalOutbound ?? "remote";
            routeObj["auto_detect_interface"] = AutoDetectInterface;
            routeObj["default_domain_resolver"] = DefaultDomainResolver ?? "local";

            if (IsAdvancedMode)
                ConfigText = ConfigJsonObject.ToJsonString(IndentedOptions);
        }
    }

    // ========== 命令辅助类 ==========
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;
        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public async void Execute(object? parameter) => await _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;
        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;
        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;
        public void Execute(object? parameter) => _execute((T?)parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }


}
