using System.Text;

namespace Jester.Host.Services;

internal static class MihomoStream
{
    // Mihomo's HTTP traffic/memory streams are newline-delimited JSON. EventSource
    // only dispatches messages after an SSE data field and a blank line.
    internal static async Task WriteSseAsync(Stream source, Stream destination, CancellationToken ct)
    {
        using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var frame = Encoding.UTF8.GetBytes($"data: {line}\n\n");
            await destination.WriteAsync(frame, ct);
            await destination.FlushAsync(ct);
        }
    }
}
