using System;
using System.Threading.Tasks;

namespace singC.Helpers;

// Persistence is attempted even when stopping fails. Closing is the caller's
// decision only after this sequence completes successfully.
public static class ShutdownSequence
{
    public static async Task RunAsync(Func<Task> stop, Action save, Func<Task> cleanup)
    {
        try { await stop(); }
        finally { save(); }
        await cleanup();
    }
}
