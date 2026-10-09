using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models;

internal sealed record ConfigCheckOutput(int ExitCode, string Output, string Error);

internal static class ConfigCheckRunner
{
    internal static async Task<string> CaptureAsync(TextReader reader, int limit, CancellationToken token)
    {
        var captured = new StringBuilder();
        var buffer = new char[4096];
        int count;
        // Keep draining after the display limit so the child's pipe cannot deadlock.
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            captured.Append(buffer, 0, Math.Min(count, Math.Max(0, limit - captured.Length)));
        return captured.ToString();
    }

    internal static async Task<ConfigCheckOutput> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken token)
    {
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("无法启动 sing-box 进行配置校验。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var output = CaptureAsync(process.StandardOutput, 1201, deadline.Token);
        var error = CaptureAsync(process.StandardError, 1201, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error);
            return new(process.ExitCode, output.Result, error.Result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("配置校验超时。");
        }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // Observe both readers even on failure; cancellation stops inherited open pipes.
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }
}
