using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using singC.Helpers;
using singC.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace singC.Pages
{
    public sealed class HomeConfigOption(string fullPath)
    {
        public string FullPath { get; } = fullPath;
        public string DisplayName { get; } = Path.GetFileName(fullPath);
    }

    public sealed partial class HomePage : Page
    {
        private const string ConfigPathsKey = "ConfigPaths";
        private readonly SingBoxService _singBox = SingBoxService.Instance;
        private readonly TrafficStatisticsViewModel _traffic = ConnectionViewModel.Instance.Traffic;
        private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(4) };
        private bool _isLoadingConfigPaths;
        private bool _isOperating;
        private bool _syncingMode = true;
        private bool _isPageLoaded;
        private string _operationStatus = string.Empty;
        private string? _shownRuntimeError;
        public ObservableCollection<HomeConfigOption> ConfigPaths { get; } = new();

        public HomePage()
        {
            InitializeComponent();
            Loaded += HomePage_Loaded;
            Unloaded += HomePage_Unloaded;
            _feedbackTimer.Tick += (_, _) => ClearFeedback();
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isPageLoaded) return;
            _isPageLoaded = true;
            _singBox.StateChanged += OnSingBoxStateChanged;
            _traffic.PropertyChanged += OnTrafficChanged;
            BackgroundManager.BackgroundChanged += ApplySavedBackground;
            LoadConfigPaths();
            ApplySavedBackground();
            RefreshModeControls();
            UpdateServiceControls();
        }

        private void HomePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isPageLoaded = false;
            _singBox.StateChanged -= OnSingBoxStateChanged;
            _traffic.PropertyChanged -= OnTrafficChanged;
            BackgroundManager.BackgroundChanged -= ApplySavedBackground;
            _feedbackTimer.Stop();
        }

        private async void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingMode || _isOperating || sender is not RadioButton button) return;
            if (int.TryParse(button.Tag?.ToString(), out int mode))
                await ApplyModeAsync((ProxyMode)mode, _singBox.ProxyPort);
        }

        private void HomeViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            HomeCanvas.Height = Math.Max(600, e.NewSize.Height);
            ControlDock.Width = Math.Max(0, Math.Min(520, e.NewSize.Width - 32));
        }

        private async void ApplyProxyPortButton_Click(object sender, RoutedEventArgs e)
        {
            if (!double.IsFinite(ProxyPortBox.Value) || ProxyPortBox.Value % 1 != 0 || ProxyPortBox.Value is < 1 or > 65535)
            {
                ShowFeedback("端口无效", InfoBarSeverity.Error, "请输入 1 到 65535 之间的整数。");
                return;
            }
            await ApplyModeAsync(ProxyMode.SystemProxy, (int)ProxyPortBox.Value);
        }

        private async Task ApplyModeAsync(ProxyMode mode, int port)
        {
            if (_isOperating || _singBox.IsBusy) { RefreshModeControls(); return; }
            bool wasRunning = _singBox.IsRunning;
            BeginOperation(wasRunning ? "切换中" : "保存中");
            try
            {
                await _singBox.SwitchModeAsync(mode, port);
                ShowFeedback(wasRunning ? $"已切换至{ProxyModeConfig.DisplayName(mode)}" : "已保存，下次启动生效", InfoBarSeverity.Success);
            }
            catch (Exception ex) { ShowFeedback("模式切换失败", InfoBarSeverity.Error, ex.Message); }
            finally
            {
                _isOperating = false;
                _operationStatus = string.Empty;
                RefreshModeControls();
                UpdateServiceControls();
            }
        }

        private void RefreshModeControls()
        {
            _syncingMode = true;
            foreach (var button in ModeSelector.Children.OfType<RadioButton>())
                button.IsChecked = button.Tag?.ToString() == ((int)_singBox.PreferredMode).ToString();
            ProxyPortBox.Value = _singBox.ProxyPort;
            bool proxy = _singBox.PreferredMode == ProxyMode.SystemProxy;
            ProxyPortPanel.Visibility = proxy ? Visibility.Visible : Visibility.Collapsed;
            _syncingMode = false;
        }

        private void LoadConfigPaths()
        {
            _isLoadingConfigPaths = true;
            try
            {
                ConfigPaths.Clear();
                foreach (var path in AppSettings.GetList(ConfigPathsKey).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                    ConfigPaths.Add(new HomeConfigOption(path));
                var current = AppSettings.Get(AppSettings.PathKey.ConfigPathKey) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(current) && File.Exists(current)
                    && !ConfigPaths.Any(option => string.Equals(option.FullPath, current, StringComparison.OrdinalIgnoreCase)))
                    ConfigPaths.Add(new HomeConfigOption(current));
                ConfigComboBox.SelectedItem = ConfigPaths.FirstOrDefault(option => string.Equals(option.FullPath, current, StringComparison.OrdinalIgnoreCase));
                ToolTipService.SetToolTip(ConfigComboBox, string.IsNullOrWhiteSpace(current) ? "选择启动配置" : current);
            }
            finally { _isLoadingConfigPaths = false; }
        }

        private void ApplySavedBackground()
        {
            bool hasBackground = BackgroundManager.CurrentBackgroundBrush != null;
            Background = BackgroundManager.CurrentBackgroundBrush ?? (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
            var foreground = BackgroundManager.RecommendedForegroundBrush;
            TitleTextBlock.Foreground = foreground ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            BackgroundText.Foreground = TitleTextBlock.Foreground;
            NoBackgroundText.Foreground = foreground ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            // Restore both original placeholders when a custom background is cleared.
            BackgroundText.Visibility = hasBackground ? Visibility.Collapsed : Visibility.Visible;
            NoBackgroundText.Visibility = hasBackground ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void ToggleServiceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isOperating || _singBox.IsBusy) return;
            bool stopping = _singBox.IsRunning;
            BeginOperation(stopping ? "停止中" : "启动中");
            try
            {
                if (stopping) await _singBox.StopAsync();
                else await _singBox.StartAsync();
            }
            catch (Exception ex) { ShowFeedback(stopping ? "停止失败" : "启动失败", InfoBarSeverity.Error, ex.Message); }
            finally
            {
                _isOperating = false;
                _operationStatus = string.Empty;
                UpdateServiceControls();
            }
        }

        private void ConfigComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingConfigPaths || ConfigComboBox.SelectedItem is not HomeConfigOption option) return;
            try
            {
                AppSettings.Set(AppSettings.PathKey.ConfigPathKey, option.FullPath);
                ToolTipService.SetToolTip(ConfigComboBox, option.FullPath);
            }
            catch (Exception ex)
            {
                LoadConfigPaths();
                ShowFeedback("配置保存失败", InfoBarSeverity.Error, ex.Message);
            }
        }

        private void BeginOperation(string status)
        {
            ClearFeedback();
            _shownRuntimeError = null;
            _isOperating = true;
            _operationStatus = status;
            UpdateServiceControls();
        }

        private void ClearFeedback()
        {
            _feedbackTimer.Stop();
            OperationFeedback.IsOpen = false;
        }

        private void ShowFeedback(string title, InfoBarSeverity severity, string? details = null)
        {
            _feedbackTimer.Stop();
            OperationFeedback.Title = title;
            OperationFeedback.Severity = severity;
            ErrorDetailsText.Text = details ?? string.Empty;
            ErrorDetailsButton.Visibility = string.IsNullOrWhiteSpace(details) ? Visibility.Collapsed : Visibility.Visible;
            OperationFeedback.IsOpen = true;
            if (severity == InfoBarSeverity.Error) _shownRuntimeError = _singBox.LastError;
            if (severity == InfoBarSeverity.Success && _isPageLoaded) _feedbackTimer.Start();
        }

        private void OnSingBoxStateChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPageLoaded) UpdateServiceControls();
        });

        private void OnTrafficChanged(object? sender, PropertyChangedEventArgs e) => UpdateSpeeds();

        private void UpdateSpeeds()
        {
            bool available = _singBox.IsRunning && _traffic.HasLiveData;
            DownloadSpeedText.Text = available ? _traffic.DownloadSpeed : "—";
            UploadSpeedText.Text = available ? _traffic.UploadSpeed : "—";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DownloadSpeedText, "下载速度：" + DownloadSpeedText.Text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UploadSpeedText, "上传速度：" + UploadSpeedText.Text);
            ToolTipService.SetToolTip(SpeedPanel, _singBox.IsRunning ? _traffic.Status : "启动后显示 sing-box 实时网速");
        }

        private void UpdateServiceControls()
        {
            bool running = _singBox.IsRunning;
            bool busy = _isOperating || _singBox.IsBusy;
            string stateText = _isOperating ? _operationStatus : _singBox.State switch
            {
                SingBoxRuntimeState.Starting => "启动中",
                SingBoxRuntimeState.Running => _singBox.IsBusy ? "切换中" : "运行中",
                SingBoxRuntimeState.Stopping => "停止中",
                SingBoxRuntimeState.Failed => "运行异常",
                _ => "未运行"
            };
            StateTextBlock.Text = stateText;
            UpdateSpeeds();
            ToggleServiceButton.Content = busy ? stateText : running ? "停止" : "启动";
            OperationProgress.IsActive = busy;
            OperationProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusDot.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            string statusBrush = _singBox.State == SingBoxRuntimeState.Failed ? "SystemFillColorCriticalBrush"
                : running ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush";
            StatusDot.Fill = (Brush)Application.Current.Resources[statusBrush];
            ToggleServiceButton.IsEnabled = !busy;
            ConfigComboBox.IsEnabled = !busy && !running;
            foreach (var button in ModeSelector.Children.OfType<RadioButton>()) button.IsEnabled = !busy;
            ProxyPortBox.IsEnabled = !busy;
            ApplyProxyPortButton.IsEnabled = !busy;
            if (!busy && _singBox.State == SingBoxRuntimeState.Failed && !string.IsNullOrWhiteSpace(_singBox.LastError)
                && _shownRuntimeError != _singBox.LastError)
                ShowFeedback("内核运行异常", InfoBarSeverity.Error, _singBox.LastError);
        }
    }
}
