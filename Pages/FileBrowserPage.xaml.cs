using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using singC.Helpers;
using System;
using System.Linq;

namespace singC.Pages
{
    public sealed partial class FileBrowserPage : Page
    {
        private readonly string[] _defaultUrls;

        public FileBrowserPage()
        {
            InitializeComponent();
            _defaultUrls = new[]
            {
                AppSettings.Get(AppSettings.FileBrowserUrlKey) ?? string.Empty,
                AppSettings.Get(AppSettings.ChatUrlKey) ?? string.Empty
            }.Where(AppSettings.IsServiceUrl).Distinct().ToArray();
            DefaultUrlComboBox.ItemsSource = _defaultUrls;
            UrlTextBox.Text = AppSettings.Get(AppSettings.FileBrowserUrlKey) ?? string.Empty;
            DefaultUrlComboBox.SelectedItem = _defaultUrls.FirstOrDefault(item =>
                string.Equals(item, UrlTextBox.Text, StringComparison.OrdinalIgnoreCase));
            NavigateToCurrentUrl();
        }

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToCurrentUrl();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (FileBrowserWebView.Source != null) FileBrowserWebView.Reload();
        }

        private void DefaultUrlComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DefaultUrlComboBox.SelectedItem is string url)
            {
                UrlTextBox.Text = url;
                NavigateToCurrentUrl();
            }
        }

        private void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                NavigateToCurrentUrl();
                e.Handled = true;
            }
        }

        private void NavigateToCurrentUrl()
        {
            var url = UrlTextBox.Text.Trim();
            if (url.Length == 0)
            {
                FileBrowserWebView.Source = new Uri("about:blank");
                BrowserStatus.Text = "未配置网页，请在设置中填写服务地址或在上方输入地址。";
                return;
            }
            if (!AppSettings.IsServiceUrl(url))
            {
                BrowserStatus.Text = "请输入有效的 HTTP/HTTPS 地址。";
                return;
            }
            BrowserStatus.Text = string.Empty;
            FileBrowserWebView.Source = new Uri(url);
        }
    }
}
