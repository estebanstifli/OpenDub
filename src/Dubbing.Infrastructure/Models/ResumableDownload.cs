using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Dubbing.Infrastructure.Models;

public static class ResumableDownload
{
    public static async Task SaveAsync(HttpClient client, Uri url, string destination, long size, string sha256,
        Action<long>? progress, CancellationToken ct)
    {
        string partial = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                if (offset > size) { File.Delete(partial); offset = 0; }
                if (offset < size)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != size)
                            throw new InvalidDataException("Invalid download range.");
                    }
                    else offset = 0; // Server does not support ranges: restart safely.
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        byte[] buffer = new byte[131072]; int read;
                        var last = DateTime.MinValue;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            offset += read;
                            if (offset > size) throw new InvalidDataException("Download exceeds its pinned size.");
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            if ((DateTime.UtcNow - last).TotalMilliseconds > 150) { progress?.Invoke(offset); last = DateTime.UtcNow; }
                        }
                    }
                }
                if (new FileInfo(partial).Length != size) throw new IOException("Incomplete download; retrying.");
                string actual;
                await using (var input = File.OpenRead(partial)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                { File.Delete(partial); throw new InvalidDataException("Model checksum mismatch. Retry the download."); }
                ct.ThrowIfCancellationRequested(); File.Move(partial, destination, true); progress?.Invoke(size); return;
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && (ex is HttpRequestException || ex is IOException && ex is not InvalidDataException))
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }
}
