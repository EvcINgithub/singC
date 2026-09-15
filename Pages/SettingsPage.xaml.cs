using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using singC.ViewModels;
using singC.Helpers;
using System;

namespace singC.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = new SettingsViewModel(App.window);
            TrafficServiceUrlBox.Text = AppSettings.Get(AppSettings.TrafficServiceUrlKey) ?? string.Empty;
        }

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
