using static Stemora.Core.Strings;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Stemora.Core;

public static class Downloads
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30)
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task FetchAsync(Uri uri, string destination, string sha256, long? expectedSize,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        if (File.Exists(destination) && await IsValidAsync(destination, sha256, expectedSize, cancellationToken)) return;
        // FileShare.None provides an interprocess download lock shared by UI and CLI.
        await using var downloadLock = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var partial = destination + ".partial";
        var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (expectedSize.HasValue && existing > expectedSize.Value) { File.Delete(partial); existing = 0; }
        if (existing == expectedSize && existing > 0)
        {
            if (await IsValidAsync(partial, sha256, expectedSize, cancellationToken)) { File.Move(partial, destination, true); return; }
            File.Delete(partial); existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Stemora/0.1");
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            throw new IOException(Tr("下载断点已失效，请重试下载。"));
        }
        response.EnsureSuccessStatusCode();
        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != existing)
            throw new IOException(Tr("服务器返回了不匹配的下载区间。"));
        if (!append) existing = 0;
        var total = expectedSize ?? (response.Content.Headers.ContentLength is { } length ? existing + length : null);
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, 131072, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[131072];
            int read;
            var lastReport = Environment.TickCount64;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                existing += read;
                if (Environment.TickCount64 - lastReport >= 100)
                {
                    progress?.Report(new(existing, total, Tr("下载中")));
                    lastReport = Environment.TickCount64;
                }
            }
        }
        progress?.Report(new(existing, total, Tr("校验 SHA-256")));
        if (!await IsValidAsync(partial, sha256, expectedSize, cancellationToken))
        {
            File.Delete(partial);
            throw new InvalidDataException(Tr("下载文件的大小或 SHA-256 不匹配，文件已丢弃，请重新下载。"));
        }
        File.Move(partial, destination, true);
        progress?.Report(new(existing, total, Tr("下载完成")));
    }

    public static async Task<bool> IsValidAsync(string path, string hash, long? size, CancellationToken ct = default)
    {
        if (size.HasValue && new FileInfo(path).Length != size.Value) return false;
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        return actual.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }
}
