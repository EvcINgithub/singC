using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using singC.ViewModels;
using singC.Helpers;
using System;
using System.Linq;
using singC.Models;

namespace singC.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }
        private bool _updatingControls;
        private bool _updatingLoggingControls = true;
        private bool _bandwagonBusy;

        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = new SettingsViewModel(App.window);
            try
            {
                if (AppSettings.Bandwagon.Load() is { } credentials)
                {
                    BandwagonVeidBox.Text = credentials.Veid;
                    BandwagonApiKeyBox.Password = credentials.ApiKey;
                }
            }
            catch (Exception) { BandwagonStatus.Text = "凭据无法读取，请重新填写并保存。"; }
            LoggingToggle.IsOn = AppSettings.Logging.Enabled;
            _updatingLoggingControls = false;
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

        private void LoggingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingLoggingControls) return;
            try
            {
                AppSettings.Logging.Enabled = LoggingToggle.IsOn;
                LoggingStatus.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                _updatingLoggingControls = true;
                try { LoggingToggle.IsOn = AppSettings.Logging.Enabled; }
                finally { _updatingLoggingControls = false; }
                LoggingStatus.Text = "日志开关保存失败：" + ex.Message;
                LoggingStatus.Visibility = Visibility.Visible;
            }
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.AddRuleCommand.Execute(null);
            if (ViewModel.RouteRules.LastOrDefault() is { } rule)
                DispatcherQueue.TryEnqueue(() =>
                {
                    RulesList.UpdateLayout();
                    if (RulesList.ContainerFromItem(rule) is FrameworkElement item)
                        item.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
                });
        }
        private void RemoveRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RouteRule rule }) ViewModel.RemoveRuleCommand.Execute(rule);
        }
        private void MoveRuleUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RouteRule rule }) ViewModel.MoveRuleUpCommand.Execute(rule);
        }
        private void MoveRuleDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RouteRule rule }) ViewModel.MoveRuleDownCommand.Execute(rule);
        }
        private void AddCondition_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RouteRule rule }) rule.AddCondition();
        }
        private void RemoveCondition_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RouteMatchCondition condition })
                ViewModel.RouteRules.FirstOrDefault(r => r.Conditions.Contains(condition))?.Conditions.Remove(condition);
        }

        private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_updatingControls) AppUpdateService.Instance.AutoCheck = AutoUpdateToggle.IsOn;
        }
        private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await AppUpdateService.Instance.CheckAsync();
        private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await AppUpdateService.Instance.ConfirmInstallAsync();
        private void CancelUpdate_Click(object sender, RoutedEventArgs e) => AppUpdateService.Instance.CancelDownload();

        private async void SaveBandwagon_Click(object sender, RoutedEventArgs e)
        {
            if (_bandwagonBusy) return;
            _bandwagonBusy = true;
            SaveBandwagonButton.IsEnabled = false;
            RefreshBandwagonButton.IsEnabled = false;
            try
            {
                AppSettings.Bandwagon.Save(BandwagonVeidBox.Text, BandwagonApiKeyBox.Password);
                ApiQuotaOutput.Text = "API 余量尚未查询。";
                BandwagonStatus.Text = AppSettings.Bandwagon.Load() == null ? "已停用套餐查询。" : "凭据已加密保存，正在查询。";
                if (App.window is MainWindow mainWindow) await mainWindow.RefreshTrafficAsync(manual: true);
                BandwagonStatus.Text = AppSettings.Bandwagon.Load() == null ? "已停用套餐查询。" : "凭据已加密保存，查询结果见侧栏。";
            }
            catch (ArgumentException ex) { BandwagonStatus.Text = ex.Message; }
            catch (Exception)
            {
                BandwagonStatus.Text = "保存或读取失败，请检查本机设置文件是否可写。";
            }
            finally
            {
                _bandwagonBusy = false;
                SaveBandwagonButton.IsEnabled = true;
                RefreshBandwagonButton.IsEnabled = true;
            }
        }

        private async void RefreshBandwagon_Click(object sender, RoutedEventArgs e)
        {
            if (_bandwagonBusy) return;
            _bandwagonBusy = true;
            SaveBandwagonButton.IsEnabled = false;
            RefreshBandwagonButton.IsEnabled = false;
            var requestedVersion = AppSettings.Bandwagon.Version;
            try
            {
                var credentials = AppSettings.Bandwagon.Load();
                if (credentials == null)
                {
                    BandwagonStatus.Text = "请先填写并保存 VEID 和 API Key。";
                    ApiQuotaOutput.Text = "API 余量尚未查询。";
                    return;
                }
                BandwagonStatus.Text = "正在刷新流量与 API 余量…";
                if (App.window is MainWindow mainWindow) await mainWindow.RefreshTrafficAsync(manual: true);
                if (requestedVersion != AppSettings.Bandwagon.Version)
                {
                    BandwagonStatus.Text = "已保存的凭据已变更，请重新刷新。";
                    ApiQuotaOutput.Text = "API 余量尚未查询。";
                    return;
                }
                ApiQuotaOutput.Text = "正在查询 API 余量…";
                var result = await BandwagonQueryCoordinator.Instance.RefreshQuotaAsync(credentials);
                if (requestedVersion != AppSettings.Bandwagon.Version)
                {
                    BandwagonStatus.Text = "已保存的凭据已变更，请重新刷新。";
                    ApiQuotaOutput.Text = "API 余量尚未查询。";
                    return;
                }
                ApiQuotaOutput.Text = result.Quota is { } quota
                    ? $"API 余量快照：15 分钟 {quota.Remaining15Minutes} 点 / 24 小时 {quota.Remaining24Hours} 点\n记录于 {result.QuotaCheckedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}。点数不等于调用次数，其他客户端也可能消耗点数。"
                    : "API 余量未知。";
                if (result.QuotaStatus.Length > 0) ApiQuotaOutput.Text += "\n" + result.QuotaStatus;
                if (result.Cached) ApiQuotaOutput.Text += $"\n使用缓存，最早可重新查询：{result.NextQuotaAt.ToLocalTime():MM-dd HH:mm:ss}";
                BandwagonStatus.Text = result.Traffic?.HasTrafficData == true
                    ? $"当前流量：已用 {result.Traffic.UsedGB:0.##} GB，剩余 {result.Traffic.RemainingGB:0.##} GB。\n更新于 {result.TrafficUpdatedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : "尚无有效流量数据。";
                if (result.Status.Length > 0) BandwagonStatus.Text += "\n" + result.Status;
            }
            catch (Exception)
            {
                BandwagonStatus.Text = "刷新失败，请检查已保存的凭据或稍后重试。";
                ApiQuotaOutput.Text = "API 余量查询未完成。";
            }
            finally
            {
                _bandwagonBusy = false;
                SaveBandwagonButton.IsEnabled = true;
                RefreshBandwagonButton.IsEnabled = true;
            }
        }
    }
}
