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

        public string FinalOutbound
        {
            get => _finalOutbound;
            set
            {
                if (_finalOutbound == value) return;
                _finalOutbound = value;
                OnPropertyChanged();
                MarkConfigDirty();

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
        private bool _isAdvancedMode;
        public bool IsAdvancedMode
        {
            get => _isAdvancedMode;
            set
            {
                if (_isAdvancedMode == value) return;
                if (value)
                {
                    try
                    {
                        _configText = GetConfigTextForSave();
                        OnPropertyChanged(nameof(ConfigText));
                    }
                    catch (Exception ex) { StatusMessage = ex.Message; OnPropertyChanged(); return; }
                }
                else if (!ParseConfigToForm()) { OnPropertyChanged(); return; }
                _isAdvancedMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEditRules));
            }
        }
        private RouteConfigDocument? _routeDocument;
        public bool CanEditRules => _routeDocument != null && !IsAdvancedMode;
        public bool NoRules => RouteRules.Count == 0;
        public string RuleCountText => $"路由规则 · {RouteRules.Count} 条";
        public ObservableCollection<string> OutboundTags { get; } = new();
        public ObservableCollection<string> DnsTags { get; } = new();

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
                _routeDocument = null;
                RouteRules.Clear();
                OnPropertyChanged(nameof(CanEditRules));
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
                if (!ParseConfigToForm())
                {
                    _isAdvancedMode = true;
                    OnPropertyChanged(nameof(IsAdvancedMode));
                    OnPropertyChanged(nameof(CanEditRules));
                    return;
                }
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
            string configText;
            try { configText = GetConfigTextForSave(); }
            catch (Exception ex) { StatusMessage = "无法检查：" + ex.Message; return; }
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

            string configText;
            try { configText = GetConfigTextForSave(); }
            catch (Exception ex) { StatusMessage = "无法保存：" + ex.Message; return; }
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
            if (IsAdvancedMode) return ConfigText;
            if (_routeDocument == null) throw new InvalidDataException("请先加载有效配置，或切换到高级 JSON 修正配置。");
            return _routeDocument.Build(RouteRules, FinalOutbound, AutoDetectInterface, DefaultDomainResolver)
                .ToJsonString(RouteConfigDocument.JsonOptions);
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

        private void AddRule()
        {
            if (!CanEditRules) { StatusMessage = "请先加载有效配置，并切换到表单模式。"; return; }
            var rule = new RouteRule();
            rule.AddCondition();
            RouteRules.Add(rule);
            StatusMessage = "已添加规则，请选择匹配类型并填写内容和出口，保存后生效。";
        }

        private void RemoveRule(RouteRule? rule)
        {
            if (rule != null && RouteRules.Remove(rule)) StatusMessage = "已删除规则，保存后生效。";
        }

        private void MoveRuleUp(RouteRule? rule)
        {
            int index = rule == null ? -1 : RouteRules.IndexOf(rule);
            if (index > 0) RouteRules.Move(index, index - 1);
        }

        private void MoveRuleDown(RouteRule? rule)
        {
            int index = rule == null ? -1 : RouteRules.IndexOf(rule);
            if (index >= 0 && index < RouteRules.Count - 1) RouteRules.Move(index, index + 1);
        }

        private readonly HashSet<RouteRule> _subscribedRules = new();
        private void RouteRules_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (var removed in _subscribedRules.Except(RouteRules).ToArray())
            {
                removed.PropertyChanged -= RouteRule_PropertyChanged;
                _subscribedRules.Remove(removed);
            }
            foreach (var added in RouteRules.Where(r => !_subscribedRules.Contains(r)))
            {
                added.PropertyChanged += RouteRule_PropertyChanged;
                _subscribedRules.Add(added);
            }
            for (int i = 0; i < RouteRules.Count; i++) RouteRules[i].Ordinal = i + 1;
            OnPropertyChanged(nameof(NoRules));
            OnPropertyChanged(nameof(RuleCountText));
            MarkConfigDirty();
        }

        private void RouteRule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(RouteRule.IsExpanded) or nameof(RouteRule.Title) or nameof(RouteRule.Summary)))
                MarkConfigDirty();
        }

        private void MarkConfigDirty()
        {
            if (!_isLoadingConfig) HasUnsavedChanges = true;
        }

        private bool ParseConfigToForm()
        {
            bool wasLoading = _isLoadingConfig;
            _isLoadingConfig = true;
            try
            {
                var document = new RouteConfigDocument(ConfigText);
                _routeDocument = document;
                RouteRules.Clear();
                foreach (var rule in document.Rules) RouteRules.Add(rule);
                _finalOutbound = document.FinalOutbound;
                _autoDetectInterface = document.AutoDetectInterface;
                _defaultDomainResolver = document.DefaultDomainResolver;
                OutboundTags.Clear();
                foreach (var tag in document.OutboundTags) OutboundTags.Add(tag);
                DnsTags.Clear();
                foreach (var tag in document.DnsTags) DnsTags.Add(tag);
                OnPropertyChanged(nameof(FinalOutbound));
                OnPropertyChanged(nameof(AutoDetectInterface));
                OnPropertyChanged(nameof(DefaultDomainResolver));
                return true;
            }
            catch (Exception ex)
            {
                _routeDocument = null;
                RouteRules.Clear();
                StatusMessage = "配置解析失败，请在高级 JSON 中修正：" + ex.Message;
                return false;
            }
            finally
            {
                _isLoadingConfig = wasLoading;
                OnPropertyChanged(nameof(CanEditRules));
            }
        }

        private void ApplySimpleSettings() => _ = SaveConfigAsync();

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
