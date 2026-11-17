using System.IO.Compression;
using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging.Abstractions;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Packages;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>夸克 stoken 过期自愈 + 通用压缩包（tar.gz/7z 扩展名）解压。</summary>
public sealed class QuarkStokenRetryTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "cc-stoken-test-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task stoken过期时刷新并重试一次()
    {
        // 服务端会过期 stoken：缓存里的旧 token 导致 404「分享的stoken过期」，
        // 应清缓存重新获取并重试，而不是把更新判失败
        var handler = new TestHttpHandler()
            .EnqueueJson("""{"status":200,"code":0,"data":{"stoken":"st-old"}}""")                // 首次取 token
            .EnqueueJson("""{"status":404,"message":"分享的stoken过期"}""", System.Net.HttpStatusCode.NotFound) // 旧 token 被拒
            .EnqueueJson("""{"status":200,"code":0,"data":{"stoken":"st-new"}}""")                // 刷新 token
            .EnqueueJson("""{"status":200,"code":0,"data":{"list":[{"file_name":"a.zip","fid":"f1","share_fid_token":"t"}]}}""");
        var api = CreateQuarkApi(handler);

        var entries = await api.ListShareAsync("pwd1", "");

        entries.Should().ContainSingle(e => e.Name == "a.zip" && e.Fid == "f1");
        handler.RequestUris.Count(u => u.Contains("/share/sharepage/token")).Should().Be(2);
        handler.RequestUris.Count(u => u.Contains("/share/sharepage/detail")).Should().Be(2);
    }

    [Fact]
    public async Task 缓存的stoken可直接复用不重复请求()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson("""{"status":200,"code":0,"data":{"stoken":"st"}}""")
            .EnqueueJson("""{"status":200,"code":0,"data":{"list":[]}}""")
            .EnqueueJson("""{"status":200,"code":0,"data":{"list":[]}}""");
        var api = CreateQuarkApi(handler);

        await api.ListShareAsync("pwd1", "");
        await api.ListShareAsync("pwd1", "");

        // 第二次调用应命中缓存：token 只请求一次
        handler.RequestUris.Count(u => u.Contains("/share/sharepage/token")).Should().Be(1);
        handler.RequestUris.Count(u => u.Contains("/share/sharepage/detail")).Should().Be(2);
    }

    [Fact]
    public async Task tar_gz压缩包可解压()
    {
        var dir = _paths.Root;
        Directory.CreateDirectory(dir);
        var tgzPath = Path.Combine(dir, "app.tar.gz");
        using (var fileStream = File.Create(tgzPath))
        using (var gzip = new GZipStream(fileStream, CompressionLevel.Fastest))
        using (var writer = new SharpCompress.Writers.Tar.TarWriter(
            gzip, new SharpCompress.Writers.Tar.TarWriterOptions(SharpCompress.Common.CompressionType.None, true)))
        {
            var bytes = Encoding.UTF8.GetBytes("MZ");
            writer.Write("应用/MRS助手.exe", new MemoryStream(bytes), DateTime.Now, bytes.Length);
        }

        var destination = Path.Combine(dir, "out");
        var extractor = new ArchiveExtractor();
        var result = await extractor.ExtractAsync(
            tgzPath, destination, new ArchiveConfig(), null, "app.tar.gz");

        result.PasswordUsed.Should().BeFalse();
        result.Files.Should().Contain(Path.Combine("应用", "MRS助手.exe"));
    }

    [Fact]
    public void 扩展名检查支持通用压缩包格式()
    {
        // PackageDetector 判定：.7z/.rar 等应识别为压缩包（ZipOverwrite）
        PackageDetector.IsArchive("x.7z").Should().BeTrue();
        PackageDetector.IsArchive("x.rar").Should().BeTrue();
        PackageDetector.IsArchive("x.tar.gz").Should().BeTrue();
    }

    private QuarkPanApi CreateQuarkApi(TestHttpHandler handler)
    {
        _paths.EnsureCreated();
        var sessions = new PanSessionStore(new SecretStore(_paths));
        sessions.SaveCookie(PanPlatform.Quark, "k=v; foo=bar");
        return new QuarkPanApi(new HttpClient(handler), sessions, NullLogger<QuarkPanApi>.Instance);
    }
}
