// Models/SingBoxService.cs
using singC.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
        private bool _stopRequested;
        private string _lastError = string.Empty;
        private int? _lastExitCode;

        public SingBoxRuntimeState State
        {
            get { lock (_stateLock) return _state; }
        }

        public bool IsRunning => State == SingBoxRuntimeState.Running;
        public bool IsBusy => State is SingBoxRuntimeState.Starting or SingBoxRuntimeState.Stopping;
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
        private SingBoxService()
        {
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken);
            try
            {
                if (State is SingBoxRuntimeState.Running or SingBoxRuntimeState.Starting)
                    return;

                SetState(SingBoxRuntimeState.Starting);
                SetLastError(string.Empty, null);
                var validation = await ValidateConfigAsync(cancellationToken: cancellationToken);
                if (!validation.IsValid)
                {
                    SetLastError(validation.ToUserMessage(), validation.ExitCode);
                    SetState(SingBoxRuntimeState.Failed);
                    throw new InvalidOperationException(validation.ToUserMessage());
                }

                string singBoxPath = AppSettings.Get(AppSettings.PathKey.SingBoxPathKey)?.Trim() ?? string.Empty;
                string configPath = AppSettings.Get(AppSettings.PathKey.ConfigPathKey)?.Trim() ?? string.Empty;
                var startInfo = new ProcessStartInfo
                {
                    FileName = singBoxPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(configPath) ?? AppContext.BaseDirectory
                };
                startInfo.ArgumentList.Add("run");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(configPath);

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.ErrorDataReceived += OnProcessErrorDataReceived;
                process.Exited += (s, e) => HandleProcessExited(process);
                lock (_stateLock)
                {
                    _process = process;
                    _stopRequested = false;
                }

                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException("进程启动失败，可能权限不足或路径错误。");
                    process.BeginErrorReadLine();
                    if (process.HasExited)
                    {
                        HandleProcessExited(process);
                        throw new InvalidOperationException(BuildExitMessage());
                    }
                    SetState(SingBoxRuntimeState.Running);
                }
                catch (Exception ex)
                {
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_process, process)) _process = null;
                        _lastError = ex.Message;
                    }
                    TryKill(process);
                    process.Dispose();
                    SetState(SingBoxRuntimeState.Failed);
                    throw new InvalidOperationException($"启动 sing-box 失败：{ex.Message}", ex);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken);
            try
            {
                Process? process;
                lock (_stateLock)
                {
                    process = _process;
                    _stopRequested = true;
                }

                if (process == null)
                {
                    SetState(SingBoxRuntimeState.Stopped);
                    return;
                }

                SetState(SingBoxRuntimeState.Stopping);
                try
                {
                    TryKill(process);
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SetLastError($"停止 sing-box 时出现异常：{ex.Message}", null);
                }
                finally
                {
                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_process, process)) _process = null;
                    }
                    process.Dispose();
                    SetState(SingBoxRuntimeState.Stopped);
                }
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public async Task<ConfigValidationResult> ValidateConfigAsync(
            string? configPath = null,
            string? configText = null,
            CancellationToken cancellationToken = default)
        {
            string singBoxPath = AppSettings.Get(AppSettings.PathKey.SingBoxPathKey)?.Trim() ?? string.Empty;
            string targetPath = configPath?.Trim()
                ?? AppSettings.Get(AppSettings.PathKey.ConfigPathKey)?.Trim()
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
                    WorkingDirectory = Path.GetDirectoryName(checkPath) ?? AppContext.BaseDirectory
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
            if (!string.IsNullOrWhiteSpace(e.Data))
                SetLastError(e.Data.Trim(), LastExitCode);
            ErrorDataReceived?.Invoke(sender, e);
        }

        private void HandleProcessExited(Process process)
        {
            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch { }

            bool isCurrent;
            bool requestedStop;
            lock (_stateLock)
            {
                isCurrent = ReferenceEquals(_process, process);
                requestedStop = _stopRequested;
                if (isCurrent)
                {
                    _process = null;
                    _lastExitCode = exitCode;
                }
            }

            if (!isCurrent) return;
            if (!requestedStop && exitCode is not null and not 0 && string.IsNullOrWhiteSpace(LastError))
                SetLastError($"sing-box 已退出，退出码：{exitCode}。", exitCode);

            SetState(requestedStop ? SingBoxRuntimeState.Stopped : SingBoxRuntimeState.Failed);
            process.Dispose();
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
