using System;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Models;

public sealed class ConfigLoadCoordinator
{
    private long _generation;
    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<string?> ReadLatestAsync(string path, Func<string, Task<string>> read)
    {
        long generation = Interlocked.Increment(ref _generation);
        try
        {
            string text = await read(path);
            return generation == Interlocked.Read(ref _generation) ? text : null;
        }
        catch when (generation != Interlocked.Read(ref _generation))
        {
            // A superseded read must not replace the current page's error or content.
            return null;
        }
    }
}
