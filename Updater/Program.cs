using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using singC.Updates;

if (args.Length != 1) return 1;
var requestPath = Path.GetFullPath(args[0]);
var work = Path.GetDirectoryName(requestPath)!;
InstallTransaction? transaction = null;
UpdateRequest? request = null;
Process? launched = null;
bool parentExited = false;
bool safeToRestart = false;
try
{
    request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidDataException();
    var executable = UpdatePackage.SafePath(request.TargetDirectory, "singC.exe");
    if (!File.Exists(executable)) throw new IOException("找不到原程序。");
    UpdatePackage.ValidateDirectory(Path.Combine(work, "stage"), request.Version);
    using (var parent = Process.GetProcessById(request.ParentId))
    {
        if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks ||
            !string.Equals(parent.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新父进程校验失败。");
        File.WriteAllText(Path.Combine(work, "helper-ready"), "ready");
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
    parentExited = true;
    transaction = new(request.TargetDirectory, Path.Combine(work, "backup"));
    transaction.Apply(Path.Combine(work, "stage"), request.Version);
    safeToRestart = true;
    var readyFile = Path.Combine(work, "app-ready");
    launched = StartApp(executable, request.ResumeProxy, readyFile);
    var deadline = DateTime.UtcNow.AddSeconds(45);
    while (!File.Exists(readyFile))
    {
        if (launched.HasExited) throw new IOException("新版本启动失败。");
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("新版本启动超时。");
        await Task.Delay(250);
    }
    File.WriteAllText(Path.Combine(work, "result.txt"), "更新成功：" + request.Version);
    return 0;
}
catch (Exception ex)
{
    try
    {
        if (launched != null && !launched.HasExited)
        {
            launched.Kill(entireProcessTree: true);
            await launched.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        transaction?.Rollback();
        // Apply already rolls back write failures. A failure before Apply leaves the old version intact.
        safeToRestart = true;
    }
    catch (Exception rollbackError)
    {
        safeToRestart = false;
        File.WriteAllText(Path.Combine(work, "rollback-error.txt"), rollbackError.ToString());
    }
    File.WriteAllText(Path.Combine(work, "result.txt"), ex.ToString());
    if (parentExited && safeToRestart && request != null)
    {
        try { StartApp(Path.Combine(request.TargetDirectory, "singC.exe"), request.ResumeProxy, null); }
        catch { safeToRestart = false; }
    }
    if (parentExited)
        Native.MessageBox(IntPtr.Zero,
            (safeToRestart ? "更新失败，已恢复旧版本。" : "更新失败，请从备份恢复程序。") + "\n记录和备份位置：" + work,
            "singC 更新", 0x10);
    return 1;
}
finally { launched?.Dispose(); }

static Process StartApp(string executable, bool resumeProxy, string? readyFile)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
    if (readyFile != null) { start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(readyFile); }
    if (resumeProxy) start.ArgumentList.Add("--resume-proxy");
    return Process.Start(start) ?? throw new IOException("无法重新启动 singC。");
}

internal static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    internal static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
