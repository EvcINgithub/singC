using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Updates;

// Only readiness is observed here. No installation or process termination is performed.
public static class UpdateHandoff
{
    public static async Task WaitUntilReadyAsync(Func<bool> ready, Func<bool> exited, Func<TimeSpan> elapsed,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token)
    {
        while (!ready())
        {
            token.ThrowIfCancellationRequested();
            if (exited() || elapsed() > TimeSpan.FromSeconds(30))
                throw new IOException("更新程序准备失败，当前版本未修改。");
            await delay(TimeSpan.FromMilliseconds(100), token);
        }
    }
}
