// Pages/HomePage.xaml.cs
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using singC.Helpers;
using singC.Models;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace singC.Pages
{
    public sealed partial class HomePage : Page
    {
        private const string ConfigPathsKey = "ConfigPaths";
        private readonly SingBoxService _singBox = SingBoxService.Instance;
        private bool _isLoadingConfigPaths;
        private bool _isOperating;
        private bool _syncingMode = true;
        public ObservableCollection<string> ConfigPaths { get; } = new();

        public HomePage()
        {
            this.InitializeComponent();
            _singBox.StateChanged += OnSingBoxStateChanged;
            BackgroundManager.BackgroundChanged += ApplySavedBackground;
            Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= HomePage_Loaded;
            LoadConfigPaths();
            ApplySavedBackground();
            OnSingBoxStateChanged();
        }

        private async void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingMode || _isOperating || ModeSelector.SelectedIndex < 0) return;
            await ApplyModeAsync((ProxyMode)ModeSelector.SelectedIndex);
        }

        private async void ApplyProxyPortButton_Click(object sender, RoutedEventArgs e)
            => await ApplyModeAsync(ProxyMode.SystemProxy);

        private async System.Threading.Tasks.Task ApplyModeAsync(ProxyMode mode)
        {
            if (_isOperating || _singBox.IsBusy) { RefreshModeControls(); return; }
            if (!double.IsFinite(ProxyPortBox.Value) || ProxyPortBox.Value % 1 != 0 || ProxyPortBox.Value is < 1 or > 65535)
            {
                ModeOperationText.Text = "端口必须是 1 到 65535 之间的整数。";
                RefreshModeControls();
                return;
            }
            _isOperating = true;
            bool wasRunning = _singBox.IsRunning;
            ModeOperationText.Text = wasRunning ? "正在校验并切换模式…" : "正在保存模式…";
            OnSingBoxStateChanged();
            try
            {
                await _singBox.SwitchModeAsync(mode, (int)ProxyPortBox.Value);
                ModeOperationText.Text = wasRunning
                    ? $"已切换至{ProxyModeConfig.DisplayName(mode)}。"
                    : $"下次启动将使用{ProxyModeConfig.DisplayName(mode)}。";
            }
            catch (Exception ex) { ModeOperationText.Text = ex.Message; }
            finally
            {
                _isOperating = false;
                RefreshModeControls();
                OnSingBoxStateChanged();
            }
        }

        private void RefreshModeControls()
        {
            _syncingMode = true;
            ModeSelector.SelectedIndex = (int)_singBox.PreferredMode;
            ProxyPortBox.Value = _singBox.ProxyPort;
            ProxyPortPanel.Visibility = _singBox.PreferredMode == ProxyMode.SystemProxy ? Visibility.Visible : Visibility.Collapsed;
            ModeHintText.Text = _singBox.PreferredMode switch
            {
                ProxyMode.Tun => "通过虚拟网卡接管流量，需要管理员权限；运行时关闭系统代理，停止后恢复。",
                ProxyMode.SystemProxy => $"HTTP/SOCKS 入口：127.0.0.1:{_singBox.ProxyPort}。自动设置 Windows 系统代理。",
                _ => "保留原配置的入站方式，也可直接选择 TUN 或系统代理。"
            };
            _syncingMode = false;
        }

        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _singBox.StateChanged -= OnSingBoxStateChanged;
            BackgroundManager.BackgroundChanged -= ApplySavedBackground;
            base.OnNavigatedFrom(e);
        }

        private void LoadConfigPaths()
        {
            ConfigPaths.Clear();

            foreach (var path in AppSettings.GetList(ConfigPathsKey))
                ConfigPaths.Add(path);

            var currentConfigPath = AppSettings.Get(AppSettings.PathKey.ConfigPathKey) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(currentConfigPath)
                && File.Exists(currentConfigPath)
                && !ConfigPaths.Any(path => string.Equals(path, currentConfigPath, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigPaths.Add(currentConfigPath);
            }

            _isLoadingConfigPaths = true;
            try
            {
                ConfigComboBox.SelectedItem = ConfigPaths.FirstOrDefault(path =>
                    string.Equals(path, currentConfigPath, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _isLoadingConfigPaths = false;
            }

        }

        private void ApplySavedBackground()
        {
            if (BackgroundManager.CurrentBackgroundBrush != null)
            {
                this.Background = BackgroundManager.CurrentBackgroundBrush;
                ApplyReadableForeground(BackgroundManager.RecommendedForegroundBrush);
                BackgroundText.Visibility = Visibility.Collapsed;
                NoBackgroundText.Visibility = Visibility.Collapsed;
            }
            else
            {
                this.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
                ApplyReadableForeground(null);
                NoBackgroundText.Visibility = Visibility.Visible;
            }
        }

        private void ApplyReadableForeground(Brush? foregroundBrush)
        {
            var titleBrush = foregroundBrush ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            var secondaryBrush = foregroundBrush ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

            TitleTextBlock.Foreground = titleBrush;
            BackgroundText.Foreground = titleBrush;
            NoBackgroundText.Foreground = secondaryBrush;
            StateTextBlock.Foreground = secondaryBrush;
        }

        private async void ToggleServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isOperating)
                return;

            _isOperating = true;
            ToggleServiceButton.IsEnabled = false;
            ConfigComboBox.IsEnabled = false;
            StateTextBlock.Text = $"状态：{(_singBox.IsRunning ? "停止中" : "启动中")}";

            try
            {
                if (_singBox.IsRunning)
                    await _singBox.StopAsync();
                else
                    await _singBox.StartAsync();
            }
            catch (Exception ex)
            {
                StateTextBlock.Text = "状态：启动失败";
                var dialog = new ContentDialog
                {
                    Title = "操作失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                _isOperating = false;
                OnSingBoxStateChanged();
            }
        }

        private void ConfigComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingConfigPaths)
                return;

            if (ConfigComboBox.SelectedItem is string path && !string.IsNullOrWhiteSpace(path))
                AppSettings.Set(AppSettings.PathKey.ConfigPathKey, path);
        }

        private void OnSingBoxStateChanged()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                bool running = _singBox.IsRunning;
                string stateText = _singBox.State switch
                {
                    SingBoxRuntimeState.Starting => "启动中",
                    SingBoxRuntimeState.Running => "运行中",
                    SingBoxRuntimeState.Stopping => "停止中",
                    SingBoxRuntimeState.Failed => "启动失败",
                    _ => "未运行"
                };
                ToggleServiceButton.Content = _singBox.IsBusy
                    ? stateText
                    : running ? "停止 Sing-Box" : "启动 Sing-Box";
                if (!_isOperating)
                {
                    StateTextBlock.Text = _singBox.State == SingBoxRuntimeState.Failed && !string.IsNullOrWhiteSpace(_singBox.LastError)
                        ? $"状态：启动失败（{_singBox.LastError}）"
                        : $"状态：{stateText}";
                }
                ToggleServiceButton.IsEnabled = !_isOperating && !_singBox.IsBusy;
                ConfigComboBox.IsEnabled = !_isOperating && !_singBox.IsBusy && !running;
                ModeSelector.IsEnabled = !_isOperating && !_singBox.IsBusy;
                ProxyPortBox.IsEnabled = !_isOperating && !_singBox.IsBusy;
                ApplyProxyPortButton.IsEnabled = !_isOperating && !_singBox.IsBusy;
                if (!_isOperating) RefreshModeControls();
            });
        }
    }
}
