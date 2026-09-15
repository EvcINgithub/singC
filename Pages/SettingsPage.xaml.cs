using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using singC.ViewModels;
using singC.Helpers;
using System;
using singC.Models;

namespace singC.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }
        private bool _updatingControls;

        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = new SettingsViewModel(App.window);
            TrafficServiceUrlBox.Text = AppSettings.Get(AppSettings.TrafficServiceUrlKey) ?? string.Empty;
            Loaded += (_, _) => { AppUpdateService.Instance.Changed += RefreshUpdateControls; RefreshUpdateControls(); };
            Unloaded += (_, _) => AppUpdateService.Instance.Changed -= RefreshUpdateControls;
        }

        private void RefreshUpdateControls()
        {
            var service = AppUpdateService.Instance;
            _updatingControls = true;
            AppVersionText.Text = "当前版本：" + service.CurrentVersionText;
            AutoUpdateToggle.IsOn = service.AutoCheck;
            CheckUpdateButton.IsEnabled = !service.Busy;
            InstallUpdateButton.IsEnabled = service.CanInstall;
            CancelUpdateButton.Visibility = service.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            AppUpdateStatus.Text = service.Status;
            ReleaseNotesText.Text = service.Notes;
            _updatingControls = false;
        }

        private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_updatingControls) AppUpdateService.Instance.AutoCheck = AutoUpdateToggle.IsOn;
        }
        private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await AppUpdateService.Instance.CheckAsync();
        private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await AppUpdateService.Instance.ConfirmInstallAsync();
        private void CancelUpdate_Click(object sender, RoutedEventArgs e) => AppUpdateService.Instance.CancelDownload();

        private async void SaveServiceUrls_Click(object sender, RoutedEventArgs e)
        {
            var value = TrafficServiceUrlBox.Text.Trim();
            if (value.Length > 0 && !AppSettings.IsServiceUrl(value))
            {
                ServiceUrlStatus.Text = "请输入有效的 HTTP/HTTPS 地址，且不要在地址中包含用户名和密码。";
                return;
            }
            try
            {
                AppSettings.Set(AppSettings.TrafficServiceUrlKey, value);
                ServiceUrlStatus.Text = "服务地址已保存。";
                if (App.window is MainWindow mainWindow) await mainWindow.RefreshTrafficAsync();
            }
            catch (Exception)
            {
                ServiceUrlStatus.Text = "保存失败，请检查本机设置文件是否可写。";
            }
        }
    }
}
