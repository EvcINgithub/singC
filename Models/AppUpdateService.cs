using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using singC.Helpers;
using singC.Updates;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models;

public sealed class AppUpdateService
{
    public static AppUpdateService Instance { get; } = new();
    private static readonly HttpClient Client = CreateClient();
    private const string AutoKey = "AutoCheckUpdates";
    private const string LastCheckKey = "LastUpdateCheckUtc";
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _download;
    private ReleaseInfo? _release;
    public Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public string CurrentVersionText => CurrentVersion.ToString(3);
    public string Status { get; private set; } = "尚未检查更新";
    public string Notes => _release?.Notes ?? "";
    public bool Busy { get; private set; }
    public bool CanInstall => _release != null && !Busy;
    public bool CanCancel => _download != null;
    public event Action? Changed;
    public bool AutoCheck
    {
        get => AppSettings.Get(AutoKey) != "false";
        set { AppSettings.Set(AutoKey, value ? "true" : "false"); Changed?.Invoke(); }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("singC-Updater/1.0");
        return client;
    }

    public void Start(DispatcherQueue dispatcher)
    {
        if (_timer != null) return;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(1);
            if (!AutoCheck || Busy) return;
            if (DateTimeOffset.TryParse(AppSettings.Get(LastCheckKey), out var last) &&
                last <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24)) return;
            await CheckAsync();
        };
        _timer.Start();
    }

    private void SetStatus(string text) { Status = text; Changed?.Invoke(); }
    public void ReportStartupFailure(string message) => SetStatus("更新后恢复失败：" + message);

    public async Task CheckAsync()
    {
        if (Busy) return;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            SetStatus("当前仅提供 Windows x64 自动更新。");
            return;
        }
        Busy = true;
        SetStatus("正在检查更新…");
        bool prompt = false;
        try
        {
            AppSettings.Set(LastCheckKey, DateTimeOffset.UtcNow.ToString("O"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Client.GetAsync(ReleaseInfo.LatestUrl, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _release = null;
                SetStatus("暂未发布正式版本。");
                return;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("GitHub 暂时限制请求，请稍后重试。");
            response.EnsureSuccessStatusCode();
            _release = ReleaseInfo.Parse((await response.Content.ReadAsStringAsync(timeout.Token)).TrimStart('\uFEFF'), CurrentVersion);
            SetStatus(_release == null ? "当前已是最新版本。" : $"发现新版本 {_release.Version.ToString(3)}");
            prompt = _release != null;
        }
        catch (Exception ex) { SetStatus("检查失败：" + ex.Message); }
        finally { Busy = false; Changed?.Invoke(); }
        if (prompt) await ConfirmInstallAsync();
    }

    public async Task ConfirmInstallAsync()
    {
        if (!CanInstall || App.window.Content is not FrameworkElement root || root.XamlRoot == null) return;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = $"更新到 {_release!.Version.ToString(3)}",
                Content = "下载完成后将重启 singC，代理连接会短暂中断。是否继续？",
                PrimaryButtonText = "下载并重启更新", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await InstallAsync();
        }
        catch (Exception ex) { SetStatus("请在设置页安装更新：" + ex.Message); }
    }

    public void CancelDownload() => _download?.Cancel();

    private async Task InstallAsync()
    {
        if (!CanInstall) return;
        var release = _release!;
        Busy = true;
        bool resume = false;
        bool stopped = false;
        bool handedOff = false;
        Process? helper = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _download = cancellation;
        SetStatus("正在下载更新…");
        try
        {
            string target = AppContext.BaseDirectory;
            string updater = Path.Combine(target, "singC.Updater.exe");
            if (!File.Exists(updater)) throw new IOException("缺少更新程序，请先手动安装完整发布包。");
            // Test permissions before stopping the app. Do not request elevation automatically.
            var probe = UpdatePackage.SafePath(target, ".singC-update-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "singC", "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var hashText = await Client.GetStringAsync(release.HashUrl, cancellation.Token);
            if (hashText.Length > 1024) throw new InvalidDataException("校验文件过大。");
            var hash = ReleaseInfo.ParseHash(hashText, release.PackageName);
            var zipPath = Path.Combine(work, "package.zip");
            using (var response = await Client.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                using var input = await response.Content.ReadAsStreamAsync(cancellation.Token);
                using var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long received = 0;
                var progressClock = Stopwatch.StartNew();
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation.Token)) > 0)
                {
                    received += count;
                    if (received > 512L * 1024 * 1024) throw new InvalidDataException("更新包超过大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation.Token);
                    if (progressClock.ElapsedMilliseconds >= 250)
                    {
                        SetStatus(total > 0 ? $"下载中：{received * 100 / total}%" : $"下载中：{received / 1024 / 1024} MB");
                        progressClock.Restart();
                    }
                }
                if (total.HasValue && received != total.Value) throw new IOException("更新包下载不完整。");
            }
            SetStatus("正在校验更新包…");
            await Task.Run(() => UpdatePackage.Extract(zipPath, Path.Combine(work, "stage"), hash, release.Version.ToString(3), cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _download = null;
            SetStatus("正在准备重启…");
            if (SingBoxService.Instance.IsBusy) throw new InvalidOperationException("代理正在启动或停止，请稍后安装。");
            resume = SingBoxService.Instance.IsRunning;
            File.Copy(updater, Path.Combine(work, "singC.Updater.exe"));
            using var current = Process.GetCurrentProcess();
            var request = new UpdateRequest(target, release.Version.ToString(3), current.Id, current.StartTime.ToUniversalTime().Ticks, resume);
            var requestPath = Path.Combine(work, "request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
            var start = new ProcessStartInfo(Path.Combine(work, "singC.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
            start.ArgumentList.Add(requestPath);
            helper = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
            var wait = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(work, "helper-ready")))
            {
                if (helper.HasExited || wait.Elapsed > TimeSpan.FromSeconds(30)) throw new IOException("更新程序准备失败，当前版本未修改。");
                await Task.Delay(100);
            }
            if (resume)
            {
                await SingBoxService.Instance.StopForUpdateAsync();
                stopped = true;
            }
            handedOff = true;
            App.Current.Exit();
        }
        catch (OperationCanceledException) { SetStatus("下载已取消或超时，当前版本未修改。"); }
        catch (UnauthorizedAccessException) { SetStatus("安装目录不可写，请将程序移至可写目录后更新。"); }
        catch (Exception ex) { SetStatus("更新失败：" + ex.Message); }
        finally
        {
            // Exit normally terminates the process; on an aborted handoff, stop the waiting helper.
            if (helper != null && !handedOff)
            {
                try { if (!helper.HasExited) helper.Kill(); } catch { }
                helper.Dispose();
            }
            if (stopped && resume && !handedOff)
            {
                try { await SingBoxService.Instance.StartAsync(); } catch { SetStatus("更新中断，请手动重新启动代理。"); }
            }
            _download = null;
            Busy = false;
            Changed?.Invoke();
        }
    }
}
