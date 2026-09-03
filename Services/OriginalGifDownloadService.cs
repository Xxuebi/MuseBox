using System.Net.Http;

namespace ScreenshotCollector.Services;

public static class OriginalGifDownloadService
{
    private const int MaximumBytes = 128 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
    public static Task<byte[]> DownloadAsync(Uri uri, CancellationToken token = default) => DownloadAsync(uri, Client, token);

    internal static async Task<byte[]> DownloadAsync(Uri uri, HttpClient client, CancellationToken token)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("GIF 原图地址无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("image/gif");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("GIF 文件超过 128 MiB。");
        using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await content.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (copy.Length + count > MaximumBytes) throw new InvalidDataException("GIF 文件超过 128 MiB。");
            await copy.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        var bytes = copy.ToArray();
        if (ImageFileFormatService.FromHeader(bytes) != ".gif")
            throw new InvalidDataException("网页返回的不是 GIF 原图，请将原 GIF 文件保存后拖入。");
        return bytes;
    }
}
