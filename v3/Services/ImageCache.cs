using System.Collections.Concurrent;

namespace CskinNative.Services;

public static class ImageCache
{
    private const int MaxEntries = 160;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> Order = new();

    public static async Task<Image?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var bytes = await Cache.GetOrAdd(url, DownloadAsync).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }
        catch { return null; }
    }

    public static async Task<Image?> LoadAsync(string? primaryUrl, string? fallbackUrl)
    {
        var image = await LoadAsync(primaryUrl).ConfigureAwait(false);
        if (image is not null || string.IsNullOrWhiteSpace(fallbackUrl)
            || string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
            return image;
        return await LoadAsync(fallbackUrl).ConfigureAwait(false);
    }

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            var bytes = File.Exists(url)
                ? await File.ReadAllBytesAsync(url)
                : await Http.GetByteArrayAsync(url);
            if (bytes.Length == 0) return null;
            Order.Enqueue(url);
            Trim();
            return bytes;
        }
        catch { return null; }
    }

    private static void Trim()
    {
        while (Cache.Count > MaxEntries && Order.TryDequeue(out var oldest))
            Cache.TryRemove(oldest, out _);
        }
    }
