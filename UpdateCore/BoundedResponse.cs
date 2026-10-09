using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace singC.Updates;

public static class BoundedResponse
{
    public static async Task<string> ReadAsync(HttpContent content, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes < 1 || maximumBytes == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var stream = await content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[maximumBytes + 1];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes) throw new InvalidDataException("更新元数据超出大小限制。");
        return Encoding.UTF8.GetString(buffer, 0, count);
    }
}
