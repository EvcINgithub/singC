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
            new NavItem { Label = "网络测试", Icon = "\uE774", PageType = "NetworkTest" },
            new NavItem { Label = "文件", Icon = "\uE8B7", PageType = "FileBrowser" },
            new NavItem { Label = "设置", Icon = "\uE713", PageType = "Settings" }
        };
        public MainWindow()
        {
            this.InitializeComponent();
            _trafficTimer = this.DispatcherQueue.CreateTimer();
            _trafficTimer.Interval = TimeSpan.FromHours(1);
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
            }

            if (MainSplitView.IsPaneOpen)
            {
                UsedText.Visibility = Visibility.Visible;
                RemainText.Visibility = Visibility.Visible;
                TittleText.Visibility = Visibility.Visible;
                ResetTimeText.Visibility = Visibility.Visible;
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
                case "NetworkTest": ContentFrame.Navigate(typeof(NetworkTestPage)); break;
                case "FileBrowser": ContentFrame.Navigate(typeof(FileBrowserPage)); break;
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
        internal async System.Threading.Tasks.Task RefreshTrafficAsync()
        {
            if (_trafficRefreshRunning) return;
            _trafficRefreshRunning = true;
            _trafficTimer?.Stop();
            var nextQuery = TimeSpan.FromHours(1);
            var requestedUrl = AppSettings.Get(AppSettings.TrafficServiceUrlKey);
            try
            {
                if (!AppSettings.IsServiceUrl(AppSettings.Get(AppSettings.TrafficServiceUrlKey)))
                {
                    UsedText.Text = "已使用流量: --";
                    RemainText.Text = "剩余流量: --";
                    ResetTimeText.Text = "重置时间: --";
                    UpdateTimeText.Text = "未配置流量服务，请在设置中填写地址";
                    return;
                }
                var data = await ClashWebSocketService.FetchTrafficDataAsync();
                if (requestedUrl != AppSettings.Get(AppSettings.TrafficServiceUrlKey)) return;
                if (data?.RetryAfterSeconds > 0)
                    nextQuery = TimeSpan.FromSeconds(Math.Min(data.RetryAfterSeconds, TimeSpan.FromDays(365).TotalSeconds));
                else if (data?.HasTrafficData == true && data.Cached && !data.Stale)
                    nextQuery = TimeSpan.FromSeconds(Math.Max(1, nextQuery.TotalSeconds - Math.Max(0, data.CacheAgeSeconds)));

                if (data?.HasTrafficData == true)
                {
                    UsedText.Text = $"已使用流量: {data.UsedGB} GB";
                    RemainText.Text = $"剩余流量: {data.RemainingGB} GB";
                    ResetTimeText.Text = $"重置时间: {DateTimeOffset.FromUnixTimeSeconds(data.ResetTimestamp!.Value).ToLocalTime():yyyy-MM-dd HH:mm:ss}";
                    var now = DateTimeOffset.UtcNow;
                    var age = Math.Clamp(data.CacheAgeSeconds, 0, now.ToUnixTimeSeconds());
                    var updatedAt = now.AddSeconds(-age).ToLocalTime();
                    var status = data.Stale ? "（旧缓存）" : data.Cached ? "（缓存）" : string.Empty;
                    UpdateTimeText.Text = $"更新: {updatedAt:yyyy-MM-dd HH:mm:ss}{status}";
                }
                else
                {
                    UpdateTimeText.Text = data?.Error != null
                        ? "更新: 流量服务暂时不可用"
                        : $"更新: 获取失败 ({DateTime.Now:HH:mm:ss})";
                }
                if (data?.Backoff == true || data?.RetryAfterSeconds > 0)
                    UpdateTimeText.Text += "（服务暂缓更新）";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"流量刷新错误: {ex.Message}");
                UpdateTimeText.Text = $"更新: 错误 ({DateTime.Now:HH:mm:ss})";
            }
            finally
            {
                _trafficRefreshRunning = false;
                if (_trafficTimer != null)
                {
                    _trafficTimer.Interval = nextQuery;
                    _trafficTimer.Start();
                }
                if (requestedUrl != AppSettings.Get(AppSettings.TrafficServiceUrlKey))
                    DispatcherQueue.TryEnqueue(async () => await RefreshTrafficAsync());
            }
        }

    }
}
