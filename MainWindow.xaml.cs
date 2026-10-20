using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using singC.Models;
using singC.Pages;
using singC.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using WinRT.Interop;
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace singC
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private DispatcherQueueTimer? _trafficTimer;
        private bool _startupDeferredInitialized;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandle(string lpModuleName);
        [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint LoadImage(nint hinst, nint name, int type, int cx, int cy, int fuLoad);
        [DllImport("user32")]
        private static extern bool DestroyIcon(nint hIcon);

        private const int IDI_APPLICATION = 32512;
        private const int IMAGE_ICON = 1;
        public List<NavItem> NavItems { get; } = new List<NavItem>
        {
            new NavItem { Label = "首页", Icon = "\uE80F", PageType = "Home" },
            new NavItem { Label = "日志", Icon = "\uE792", PageType = "Log" },
            new NavItem { Label = "连接", Icon = "\uE8F1", PageType = "Connections" },
            new NavItem { Label = "流量统计", Icon = "\uE9D9", PageType = "TrafficStatistics" },
            new NavItem { Label = "网络测试", Icon = "\uE774", PageType = "NetworkTest" },
            new NavItem { Label = "设置", Icon = "\uE713", PageType = "Settings" }
        };
        public MainWindow()
        {
            this.InitializeComponent();
            _trafficTimer = this.DispatcherQueue.CreateTimer();
            _trafficTimer.Interval = TimeSpan.FromHours(8);
            _trafficTimer.Tick += async (s, e) => await RefreshTrafficAsync();
            this.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                // 1. 获取当前模块句柄
                var moduleFileName = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(moduleFileName))
                    throw new InvalidOperationException("无法获取当前进程模块路径。");

                var exeHandle = GetModuleHandle(Path.GetFileName(moduleFileName) ?? string.Empty);
                // 2. 从模块中加载默认的应用图标资源
                nint iconHandle = LoadImage(exeHandle, (nint)IDI_APPLICATION, IMAGE_ICON, 16, 16, 0);
                if (iconHandle != IntPtr.Zero)
                {
                    var iconId = Win32Interop.GetIconIdFromIcon(iconHandle);
                    this.AppWindow.SetIcon(iconId);
                    DestroyIcon(iconHandle);
                }
                else
                {
                    Debug.WriteLine("未能加载默认应用图标，请检查项目是否设置了 ApplicationIcon");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"从 exe 资源加载图标失败: {ex.Message}");
                }
            });

            // Defer native resources and page construction until the window has been activated.
            this.DispatcherQueue.TryEnqueue(InitializeAfterActivation);
        }

        private void InitializeAfterActivation()
        {
            if (_startupDeferredInitialized)
                return;

            _startupDeferredInitialized = true;

            string lastPage = AppSettings.Get(AppSettings.LastPageKey) ?? "Home";
            int selectedIndex = NavItems.FindIndex(item => item.PageType == lastPage);
            NavListView.SelectionChanged -= NavListView_SelectionChanged;
            NavListView.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            NavListView.SelectionChanged += NavListView_SelectionChanged;
            NavigateToPage(selectedIndex >= 0 ? lastPage : "Home");
            _trafficTimer?.Start();
            _ = RefreshTrafficAsync();
        }
        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            MainSplitView.IsPaneOpen = !MainSplitView.IsPaneOpen;
            if (!MainSplitView.IsPaneOpen)
            {
                UsedText.Visibility = Visibility.Collapsed;
                RemainText.Visibility = Visibility.Collapsed;
                TittleText.Visibility = Visibility.Collapsed;
                ResetTimeText.Visibility = Visibility.Collapsed;
                UpdateTimeText.Visibility = Visibility.Collapsed;
                ApiQuotaText.Visibility = Visibility.Collapsed;
                RefreshTrafficButton.Visibility = Visibility.Collapsed;
                RefreshApiQuotaButton.Visibility = Visibility.Collapsed;
            }

            if (MainSplitView.IsPaneOpen)
            {
                UsedText.Visibility = Visibility.Visible;
                RemainText.Visibility = Visibility.Visible;
                TittleText.Visibility = Visibility.Visible;
                ResetTimeText.Visibility = Visibility.Visible;
                UpdateTimeText.Visibility = Visibility.Visible;
                ApiQuotaText.Visibility = Visibility.Visible;
                RefreshTrafficButton.Visibility = Visibility.Visible;
                RefreshApiQuotaButton.Visibility = Visibility.Visible;
            }
        }

        private void NavListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavListView.SelectedItem is NavItem selectedItem)
            {
                string pageType = selectedItem.PageType;

                DispatcherQueue.TryEnqueue(() =>
                {
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { AppSettings.Set(AppSettings.LastPageKey, pageType); }
                        catch (Exception ex) { Debug.WriteLine($"保存上次页面失败: {ex.Message}"); }
                    });
                    NavigateToPage(pageType);
                });
            }
        }

        private void NavigateToPage(string pageType)
        {
            switch (pageType)
            {
                case "Home": ContentFrame.Navigate(typeof(HomePage)); break;
                case "Log": ContentFrame.Navigate(typeof(LogPage)); break;
                case "Connections": ContentFrame.Navigate(typeof(ConnectionsPage)); break;
                case "TrafficStatistics": ContentFrame.Navigate(typeof(TrafficStatisticsPage)); break;
                case "NetworkTest": ContentFrame.Navigate(typeof(NetworkTestPage)); break;
                case "Settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            }
        }

        private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key >= Windows.System.VirtualKey.Number1 && e.Key <= Windows.System.VirtualKey.Number6
                && e.KeyStatus.IsMenuKeyDown == false)
            {
                var modifiers = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
                if ((modifiers & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
                {
                    int index = (int)e.Key - (int)Windows.System.VirtualKey.Number1;
                    if (index < NavItems.Count) NavListView.SelectedIndex = index;
                    e.Handled = true;
                }
            }
        }


        //-------------------------------流量相关----------------------------------
        private bool _trafficRefreshRunning;
        private async void RefreshTraffic_Click(object sender, RoutedEventArgs e) => await RefreshTrafficAsync(manual: true);
        private async void RefreshApiQuota_Click(object sender, RoutedEventArgs e) => await RefreshTrafficAsync(queryApiQuota: true);
        internal async System.Threading.Tasks.Task RefreshTrafficAsync(bool manual = false, bool queryApiQuota = false)
        {
            if (_trafficRefreshRunning) return;
            _trafficRefreshRunning = true;
            RefreshTrafficButton.IsEnabled = false;
            RefreshApiQuotaButton.IsEnabled = false;
            _trafficTimer?.Stop();
            var nextQuery = TimeSpan.FromHours(8);
            var requestedVersion = AppSettings.Bandwagon.Version;
            try
            {
                // Clear the previous account before querying or reporting invalid credentials.
                UsedText.Text = "已使用流量: --";
                RemainText.Text = "剩余流量: --";
                ResetTimeText.Text = "重置时间: --";
                ApiQuotaText.Text = "API 余量: --";
                ToolTipService.SetToolTip(ApiQuotaText, null);
                var credentials = AppSettings.Bandwagon.Load();
                if (credentials == null)
                {
                    UpdateTimeText.Text = "请在设置中填写搬瓦工 VEID 和 API Key";
                    return;
                }
                UpdateTimeText.Text = queryApiQuota ? "正在查询 API 余量…" : "正在查询搬瓦工流量…";
                var result = queryApiQuota
                    ? await BandwagonQueryCoordinator.Instance.RefreshQuotaAsync(credentials)
                    : await BandwagonQueryCoordinator.Instance.RefreshTrafficAsync(credentials, manual);
                if (requestedVersion != AppSettings.Bandwagon.Version) return;
                if (result.NextAutomaticAt != DateTimeOffset.MinValue)
                    nextQuery = TimeSpan.FromSeconds(Math.Clamp((result.NextAutomaticAt - DateTimeOffset.UtcNow).TotalSeconds, 1, 86400));
                var data = result.Traffic;
                if (result.Quota is { } quota)
                {
                    ApiQuotaText.Text = $"API 余量快照：\n15 分钟 {quota.Remaining15Minutes} 点 / 24 小时 {quota.Remaining24Hours} 点";
                    ToolTipService.SetToolTip(ApiQuotaText, $"记录于 {result.QuotaCheckedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}；点数不等于调用次数，其他客户端也会消耗点数。");
                }
                else ToolTipService.SetToolTip(ApiQuotaText, null);
                if (result.QuotaStatus.Length > 0) ApiQuotaText.Text += "\n" + result.QuotaStatus;

                if (data?.HasTrafficData == true)
                {
                    UsedText.Text = $"已使用流量: {data.UsedGB:0.##} GB";
                    RemainText.Text = $"剩余流量: {data.RemainingGB:0.##} GB";
                    ResetTimeText.Text = $"重置时间: {DateTimeOffset.FromUnixTimeSeconds(data.ResetTimestamp!.Value).ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                    UpdateTimeText.Text = $"更新: {result.TrafficUpdatedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                }
                else
                {
                    UpdateTimeText.Text = "尚无套餐流量数据。";
                }
                if (result.Status.Length > 0) UpdateTimeText.Text += "\n" + result.Status;
                if (result.Cached) UpdateTimeText.Text += $"\n使用缓存，最早可查询{(queryApiQuota ? "配额" : "流量")}：{(queryApiQuota ? result.NextQuotaAt : result.NextManualAt).ToLocalTime():MM-dd HH:mm:ss}";
                UpdateTimeText.TextWrapping = TextWrapping.Wrap;
            }
            catch (Exception)
            {
                UpdateTimeText.Text = "查询失败，请在设置中检查搬瓦工凭据。";
            }
            finally
            {
                _trafficRefreshRunning = false;
                RefreshTrafficButton.IsEnabled = true;
                RefreshApiQuotaButton.IsEnabled = true;
                if (_trafficTimer != null)
                {
                    _trafficTimer.Interval = nextQuery;
                    _trafficTimer.Start();
                }
                if (requestedVersion != AppSettings.Bandwagon.Version)
                    DispatcherQueue.TryEnqueue(async () => await RefreshTrafficAsync());
            }
        }

    }
}
