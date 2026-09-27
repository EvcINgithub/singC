// Models/SingBoxService.cs
using singC.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Security.Principal;

namespace singC.Models
{
    public enum SingBoxRuntimeState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Failed
    }

    public sealed class ConfigValidationResult
    {
        public bool IsValid { get; }
        public string Stage { get; }
        public string Message { get; }
        public string Details { get; }
        public int? ExitCode { get; }

        private ConfigValidationResult(bool isValid, string stage, string message, string details, int? exitCode)
        {
            IsValid = isValid;
            Stage = stage;
            Message = message;
            Details = details;
            ExitCode = exitCode;
        }

        public static ConfigValidationResult Success(string message, string details = "")
            => new(true, "完成", message, details, 0);

        public static ConfigValidationResult Failure(string stage, string message, string details = "", int? exitCode = null)
            => new(false, stage, message, details, exitCode);

        public string ToUserMessage()
            => string.IsNullOrWhiteSpace(Details) ? Message : $"{Message}\n{Details}";
    }

    public class SingBoxService
    {
        private static readonly Lazy<SingBoxService> _instance =
            new Lazy<SingBoxService>(() => new SingBoxService());
        public static SingBoxService Instance => _instance.Value;

        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly object _stateLock = new();
        private Process? _process;
        private SingBoxRuntimeState _state = SingBoxRuntimeState.Stopped;
        private bool _isSwitching;
        private bool _shutdownRequested;
        private long _runGeneration;
        private LaunchPlan? _activePlan;
        private readonly SystemProxyLease _systemProxy;
        private readonly Func<string?> _getExecutable;
        private readonly Func<string?> _getConfig;
        private readonly Action<ProxyMode, int> _saveMode;
        private readonly Func<bool> _canUseTun;
        private readonly string _runtimeDirectory;
        private ProxyMode _preferredMode;
        private int _proxyPort = 7890;
        private sealed record LaunchPlan(string Executable, string SourcePath, string RuntimePath, ProxyMode Mode, PreparedModeConfig Config);

        public ProxyMode PreferredMode => _preferredMode;
        public int ProxyPort => _proxyPort;
        public ProxyMode? ActiveMode => _activePlan?.Mode;
        public long RunGeneration => Interlocked.Read(ref _runGeneration);
        private string _lastError = string.Empty;
        private int? _lastExitCode;

        public SingBoxRuntimeState State
        {
            get { lock (_stateLock) return _state; }
        }

        public bool IsRunning => State == SingBoxRuntimeState.Running;
        public bool IsBusy => _isSwitching || State is SingBoxRuntimeState.Starting or SingBoxRuntimeState.Stopping;
        public string LastError
        {
            get { lock (_stateLock) return _lastError; }
        }

        public int? LastExitCode
        {
            get { lock (_stateLock) return _lastExitCode; }
        }

        // 当运行状态改变时触发，方便 UI 刷新
        public event Action? StateChanged;
        public event DataReceivedEventHandler? ErrorDataReceived;
        private SingBoxService() : this(
            () => AppSettings.Get(AppSettings.PathKey.SingBoxPathKey),
            () => AppSettings.Get(AppSettings.PathKey.ConfigPathKey),
            (mode, port) => AppSettings.Set(AppSettings.ProxyModeKey, $"{mode}:{port}"),
            new WindowsSystemProxy(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "singC"),
            IsAdministrator)
        {
            var preference = (AppSettings.Get(AppSettings.ProxyModeKey) ?? "").Split(':');
            if (preference.Length == 2 && Enum.TryParse(preference[0], out ProxyMode mode) && Enum.IsDefined(mode)
                && int.TryParse(preference[1], out int port) && port is >= 1 and <= 65535)
            { _preferredMode = mode; _proxyPort = port; }
        }

        internal SingBoxService(Func<string?> getExecutable, Func<string?> getConfig, Action<ProxyMode, int> saveMode,
            ISystemProxyBackend proxyBackend, string dataDirectory, Func<bool> canUseTun)
        {
            _getExecutable = getExecutable;
            _getConfig = getConfig;
            _saveMode = saveMode;
            _canUseTun = canUseTun;
            _runtimeDirectory = Path.Combine(dataDirectory, "runtime");
            _systemProxy = new(proxyBackend, Path.Combine(dataDirectory, "system-proxy-backup.json"));
            try { _systemProxy.Recover(); }
            catch (Exception ex) { _lastError = "恢复上次系统代理失败：" + ex.Message; }
        }

        private static bool IsAdministrator()
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken);
            LaunchPlan? plan = null;
            try
            {
                if (IsRunning) return;
                if (_process != null) throw new InvalidOperationException("请先停止尚未退出的 sing-box 进程。");
                SetState(SingBoxRuntimeState.Starting);
                SetLastError(string.Empty, null);
                plan = await PrepareLaunchAsync(_preferredMode, _proxyPort, null, cancellationToken);
                await StartPlanAsync(plan, cancellationToken);
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_activePlan, plan)) DeleteRuntime(plan);
                SetLastError(ex.Message, LastExitCode);
                SetState(SingBoxRuntimeState.Failed);
                throw;
            }
            finally { _operationGate.Release(); }
        }

        public async Task SwitchModeAsync(ProxyMode mode, int proxyPort, CancellationToken cancellationToken = default)
        {
            if (!Enum.IsDefined(mode) || proxyPort is < 1 or > 65535)
                throw new ArgumentException("请选择有效的运行模式和代理端口。");
            await _operationGate.WaitAsync(cancellationToken);
            LaunchPlan? candidate = null;
            LaunchPlan? previous = null;
            bool stopped = false;
            try
            {
                if (_shutdownRequested) throw new InvalidOperationException("应用正在退出。");
                if (_preferredMode == mode && _proxyPort == proxyPort) return;
                _isSwitching = true;
                SetState(State);
                if (!IsRunning)
                {
                    if (_process != null) throw new InvalidOperationException("请先停止尚未退出的 sing-box 进程。");
                    SaveMode(mode, proxyPort);
                    return;
                }
                previous = _activePlan!;
                candidate = await PrepareLaunchAsync(mode, proxyPort, previous, cancellationToken);
                // Once the old process is stopped, complete the switch or rollback as a transaction.
                cancellationToken.ThrowIfCancellationRequested();
                await StopCoreAsync(keepRuntime: true, CancellationToken.None);
                stopped = true;
                await StartPlanAsync(candidate, CancellationToken.None);
                SaveMode(mode, proxyPort);
                DeleteRuntime(previous);
            }
            catch (Exception ex)
            {
                if (previous != null && (stopped || _activePlan == null))
                {
                    try
                    {
                        await StopCoreAsync(keepRuntime: true, CancellationToken.None);
                        await StartPlanAsync(previous, CancellationToken.None);
                    }
                    catch (Exception rollback)
                    {
                        if (!ReferenceEquals(_activePlan, previous)) DeleteRuntime(previous);
                        SetLastError($"切换失败：{ex.Message}；恢复原模式也失败：{rollback.Message}", null);
                        SetState(SingBoxRuntimeState.Failed);
                        throw new InvalidOperationException(LastError, ex);
                    }
                    SetLastError($"切换失败，已恢复{ProxyModeConfig.DisplayName(previous.Mode)}：{ex.Message}", null);
                    throw new InvalidOperationException(LastError, ex);
                }
                throw;
            }
            finally
            {
                if (!ReferenceEquals(_activePlan, candidate)) DeleteRuntime(candidate);
                _isSwitching = false;
                SetState(State);
                _operationGate.Release();
            }
        }

        private void SaveMode(ProxyMode mode, int port)
        {
            _saveMode(mode, port);
            _preferredMode = mode;
            _proxyPort = port;
        }

        private async Task<LaunchPlan> PrepareLaunchAsync(ProxyMode mode, int port, LaunchPlan? previous, CancellationToken token)
        {
            _systemProxy.Recover();
            string executable = previous?.Executable ?? _getExecutable()?.Trim() ?? "";
            string source = previous?.SourcePath ?? _getConfig()?.Trim() ?? "";
            if (!File.Exists(executable)) throw new FileNotFoundException("未找到 sing-box 可执行文件，请先在设置中选择。");
            if (!File.Exists(source)) throw new FileNotFoundException("未找到 sing-box 配置文件，请先选择配置。");
            var config = ProxyModeConfig.Build(await File.ReadAllTextAsync(source, token), mode, port);
            if (config.HasTun && !_canUseTun())
                throw new InvalidOperationException("TUN 模式需要管理员权限，请以管理员身份运行 singC 后重试。");
            string directory = _runtimeDirectory;
            Directory.CreateDirectory(directory);
            var plan = new LaunchPlan(executable, source, Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), mode, config);
            try
            {
                await File.WriteAllTextAsync(plan.RuntimePath, config.Json, new UTF8Encoding(false), token);
                var validation = await ValidateConfigAsync(plan.RuntimePath, cancellationToken: token,
                    workingDirectory: Path.GetDirectoryName(source), executablePath: executable);
                if (!validation.IsValid) throw new InvalidOperationException(validation.ToUserMessage());
                token.ThrowIfCancellationRequested();
                return plan;
            }
            catch { DeleteRuntime(plan); throw; }
        }

        private async Task StartPlanAsync(LaunchPlan plan, CancellationToken token)
        {
            if (_shutdownRequested) throw new InvalidOperationException("应用正在退出。");
            if (_process != null) throw new InvalidOperationException("上一个内核进程尚未停止。");
            SetState(SingBoxRuntimeState.Starting);
            SetLastError(string.Empty, null);
            var startInfo = new ProcessStartInfo
            {
                FileName = plan.Executable, UseShellExecute = false, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(plan.SourcePath) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(plan.RuntimePath);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.ErrorDataReceived += OnProcessErrorDataReceived;
            process.Exited += (sender, args) => { _ = HandleProcessExitedAsync(process); };
            lock (_stateLock) { _process = process; _activePlan = plan; }
            bool started = false;
            try
            {
                if (!process.Start()) throw new InvalidOperationException("无法启动 sing-box。");
                started = true;
                process.BeginErrorReadLine();
                await Task.Delay(1000, token);
                if (process.HasExited) throw new InvalidOperationException(BuildExitMessage());
                if (plan.Config.ProxyPort is { } port)
                {
                    var wait = Stopwatch.StartNew();
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (process.HasExited) throw new InvalidOperationException(BuildExitMessage());
                        try
                        {
                            using var client = new TcpClient();
                            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                            await client.ConnectAsync(plan.Config.ProxyHost!, port, probeTimeout.Token);
                            break;
                        }
                        catch (Exception ex) when ((ex is SocketException or OperationCanceledException) && !token.IsCancellationRequested)
                        {
                            if (wait.Elapsed > TimeSpan.FromSeconds(10))
                                throw new IOException("代理端口未就绪，请检查端口占用或 sing-box 日志。", ex);
                            await Task.Delay(100, token);
                        }
                    }
                }
                if (process.HasExited) throw new InvalidOperationException(BuildExitMessage());
                if (_shutdownRequested) throw new InvalidOperationException("应用正在退出。");
                if (plan.Config.ProxyPort != null || plan.Config.DisableSystemProxy)
                    _systemProxy.Apply(plan.Config.ProxyHost, plan.Config.ProxyPort);
                Interlocked.Increment(ref _runGeneration);
                SetState(SingBoxRuntimeState.Running);
            }
            catch
            {
                if (started)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                lock (_stateLock) { _process = null; _activePlan = null; }
                try { _systemProxy.Restore(); } finally { process.Dispose(); }
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken);
            try { await StopCoreAsync(keepRuntime: false, cancellationToken); }
            finally { _operationGate.Release(); }
        }

        private async Task StopCoreAsync(bool keepRuntime, CancellationToken token)
        {
            var process = _process;
            var plan = _activePlan;
            if (process != null)
            {
                SetState(SingBoxRuntimeState.Stopping);
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
                }
                catch (Exception ex)
                {
                    SetLastError("停止 sing-box 失败：" + ex.Message, null);
                    SetState(SingBoxRuntimeState.Failed);
                    throw;
                }
                lock (_stateLock) { _process = null; _activePlan = null; }
                process.Dispose();
            }
            try { _systemProxy.Restore(); }
            finally
            {
                if (!keepRuntime) DeleteRuntime(plan);
                SetState(SingBoxRuntimeState.Stopped);
            }
        }

        public void PrepareForExit()
        {
            _shutdownRequested = true;
            try { _systemProxy.Restore(); }
            catch (Exception ex) { Debug.WriteLine("系统代理将在下次启动时恢复：" + ex.Message); }
        }

        private static void DeleteRuntime(LaunchPlan? plan)
        {
            if (plan == null) return;
            try { File.Delete(plan.RuntimePath); } catch { }
        }

        public async Task StopForUpdateAsync()
        {
            Process? observed = null;
            lock (_stateLock)
            {
                if (_process != null)
                {
                    try { observed = Process.GetProcessById(_process.Id); }
                    catch (ArgumentException) { }
                }
            }
            using (observed)
            {
                await StopAsync();
                if (observed != null && !observed.HasExited)
                    throw new IOException("sing-box 未能退出，已取消安装。");
            }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public async Task<ConfigValidationResult> ValidateConfigAsync(
            string? configPath = null,
            string? configText = null,
            CancellationToken cancellationToken = default,
            string? workingDirectory = null,
            string? executablePath = null)
        {
            string singBoxPath = executablePath ?? _getExecutable()?.Trim() ?? string.Empty;
            string targetPath = configPath?.Trim()
                ?? _getConfig()?.Trim()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(singBoxPath) || !File.Exists(singBoxPath))
                return ConfigValidationResult.Failure("路径", "未找到 sing-box 可执行文件。", singBoxPath);
            if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                return ConfigValidationResult.Failure("路径", "未找到配置文件。", targetPath);

            string text;
            try
            {
                text = configText ?? await File.ReadAllTextAsync(targetPath, cancellationToken);
                using var document = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                return ConfigValidationResult.Failure("JSON", "配置 JSON 格式无效。", Trim(ex.Message, 600));
            }
            catch (OperationCanceledException)
            {
                return ConfigValidationResult.Failure("取消", "配置校验已取消。");
            }
            catch (Exception ex)
            {
                return ConfigValidationResult.Failure("读取", "读取配置失败。", Trim(ex.Message, 600));
            }

            string? temporaryPath = null;
            Process? process = null;
            try
            {
                string checkPath = targetPath;
                if (configText != null)
                {
                    string directory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
                    temporaryPath = Path.Combine(directory, $".singc-check-{Guid.NewGuid():N}.json");
                    await File.WriteAllTextAsync(temporaryPath, text, new UTF8Encoding(false), cancellationToken);
                    checkPath = temporaryPath;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = singBoxPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(checkPath) ?? AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add("check");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(checkPath);
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    return ConfigValidationResult.Failure("启动", "无法启动 sing-box 进行配置校验。");

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKill(process);
                    return ConfigValidationResult.Failure("超时", "配置校验超过 15 秒未完成。");
                }

                string output = await outputTask;
                string error = await errorTask;
                string detail = Trim(string.IsNullOrWhiteSpace(error) ? output : error, 1200);
                return process.ExitCode == 0
                    ? ConfigValidationResult.Success("配置校验成功。", detail)
                    : ConfigValidationResult.Failure("sing-box", "配置校验失败。", detail, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                if (process != null) TryKill(process);
                return ConfigValidationResult.Failure("取消", "配置校验已取消。");
            }
            catch (Exception ex)
            {
                return ConfigValidationResult.Failure("校验", "配置校验失败。", Trim(ex.Message, 1200));
            }
            finally
            {
                process?.Dispose();
                if (temporaryPath != null)
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }

        private void OnProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!ReferenceEquals(sender, _process)) return;
            if (!string.IsNullOrWhiteSpace(e.Data))
                SetLastError(e.Data.Trim(), LastExitCode);
            ErrorDataReceived?.Invoke(sender, e);
        }

        private async Task HandleProcessExitedAsync(Process process)
        {
            await _operationGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_process, process)) return;
                int? exitCode = null;
                try { exitCode = process.ExitCode; } catch { }
                var plan = _activePlan;
                lock (_stateLock) { _process = null; _activePlan = null; _lastExitCode = exitCode; }
                try { _systemProxy.Restore(); }
                catch (Exception ex) { SetLastError("内核已退出，系统代理恢复失败：" + ex.Message, exitCode); }
                DeleteRuntime(plan);
                if (string.IsNullOrWhiteSpace(LastError)) SetLastError($"sing-box 已退出，退出码：{exitCode}。", exitCode);
                process.Dispose();
                SetState(SingBoxRuntimeState.Failed);
            }
            finally { _operationGate.Release(); }
        }

        private string BuildExitMessage()
        {
            return string.IsNullOrWhiteSpace(LastError)
                ? $"sing-box 启动后立即退出，退出码：{LastExitCode?.ToString() ?? "未知"}。"
                : LastError;
        }

        private void SetState(SingBoxRuntimeState state)
        {
            lock (_stateLock) _state = state;
            try { StateChanged?.Invoke(); } catch { }
        }

        private void SetLastError(string message, int? exitCode)
        {
            lock (_stateLock)
            {
                _lastError = message;
                if (exitCode.HasValue) _lastExitCode = exitCode;
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }

        private static string Trim(string value, int maxLength)
        {
            string compact = value.ReplaceLineEndings(" ").Trim();
            return compact.Length <= maxLength ? compact : $"{compact[..maxLength]}...";
        }

    }
}
