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
            });
        }
    }
}
