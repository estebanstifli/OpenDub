using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Dubbing.Infrastructure.Models;
using Dubbing.Infrastructure.Projects;
using Dubbing.Core.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dubbing.Integration.Tests;

public sealed class OpenDubReleaseTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task PartialDownloadResumesOrRestartsAndVerifiesHash(bool ranges)
    {
        string root = Path.Combine(Path.GetTempPath(), "opendub-download-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "model.bin"); byte[] bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(path + ".partial", bytes[..12]);
        using var http = new HttpClient(new RangeHandler(bytes, ranges));
        try
        {
            await ResumableDownload.SaveAsync(http, new("https://example.invalid/model"), path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), null, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.False(File.Exists(path + ".partial"));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class RangeHandler(byte[] bytes, bool ranges) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(12, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(ranges ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(ranges ? bytes[12..] : bytes) };
            if (ranges) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(12, bytes.Length-1, bytes.Length);
            return Task.FromResult(response);
        }
    }
    [Fact]
    public async Task CopiedSourceSurvivesMovingProjectFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "opendub-move-" + Guid.NewGuid());
        string old = Path.Combine(root, "before"), moved = Path.Combine(root, "after");
        Directory.CreateDirectory(Path.Combine(old, "assets", "source"));
        string source = Path.Combine(old, "assets", "source", "source.mp4"); await File.WriteAllTextAsync(source, "fixture");
        var store = new JsonProjectStore(new ProjectJson(), NullLogger<JsonProjectStore>.Instance);
        try
        {
            await store.SaveAsync(new() { Source = new(source, "hash", TimeSpan.FromSeconds(1), 2, 48000) }, old, default);
            Assert.DoesNotContain(old.Replace("\\", "\\\\"), await File.ReadAllTextAsync(Path.Combine(old, "project.json")));
            Directory.Move(old, moved);
            var result = await store.LoadAsync(moved, default);
            Assert.Equal(Path.Combine(moved, "assets", "source", "source.mp4"), result.Source!.Path);
            Assert.True(File.Exists(result.Source.Path));
        }
        finally { Directory.Delete(root, true); }
    }
}
