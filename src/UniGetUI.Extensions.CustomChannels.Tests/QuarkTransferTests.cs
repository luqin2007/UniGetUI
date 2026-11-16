using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>夸克网盘取链与转存：客户端 UA、目录复用与同名残留清理。</summary>
public sealed class QuarkTransferTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "cc-quark-transfer-test-" + Guid.NewGuid().ToString("N")));

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
    public async Task 取直链请求携带夸克客户端UA()
    {
        // 大文件直链被 23018「download file size limit」拒绝的根因是 UA 校验
        var handler = new TestHttpHandler().EnqueueJson(
            """{"status":200,"code":0,"data":[{"download_url":"https://dl.example/file.zip"}]}""");
        var api = CreateQuarkApi(handler);

        var url = await api.GetDownloadUrlAsync("fid1");

        url.Should().Be("https://dl.example/file.zip");
        var userAgent = handler.UserAgentHeaders.Should().ContainSingle().Subject;
        userAgent.Should().Contain("quark-cloud-drive/"); // 客户端 UA
        userAgent.Should().NotContain("Mozilla");         // 浏览器 UA 已被替换
    }

    [Fact]
    public async Task EnsureFolder复用已存在的布尔dir目录()
    {
        // 线上故障：file/sort 返回 dir:true（布尔），按数字解析误判目录不存在，
        // 重复建目录报「同名冲突」
        var handler = new TestHttpHandler().EnqueueJson(
            """{"status":200,"code":0,"data":{"list":[{"file_name":"AppUpdater","dir":true,"fid":"dirfid"}]}}""");
        var api = CreateQuarkApi(handler);

        var fid = await api.EnsureFolderAsync("/AppUpdater");

        fid.Should().Be("dirfid");
        handler.RequestUris.Should().NotContain(u => u.Contains("/file?")); // 未发起建目录
    }

    [Fact]
    public async Task 数字dir字段的目录同样可复用()
    {
        var handler = new TestHttpHandler().EnqueueJson(
            """{"status":200,"code":0,"data":{"list":[{"file_name":"AppUpdater","dir":1,"fid":"dirfid"}]}}""");
        var api = CreateQuarkApi(handler);

        (await api.EnsureFolderAsync("/AppUpdater")).Should().Be("dirfid");
        handler.RequestUris.Should().NotContain(u => u.Contains("/file?"));
    }

    [Fact]
    public async Task 转存前删除目标目录中的同名残留文件()
    {
        // 上次取链失败留下的同名文件会让 ShareSave 报同名冲突：转存前先删
        var handler = new TestHttpHandler()
            .EnqueueJson("""{"status":200,"code":0,"data":{"stoken":"st"}}""")                       // 分享 token
            .EnqueueJson("""{"status":200,"code":0,"data":{"list":[{"file_name":"AppUpdater","dir":true,"fid":"dirfid"}]}}""") // 根目录找转存目录
            .EnqueueJson("""{"status":200,"code":0,"data":{"list":[{"file_name":"mrs.zip","dir":false,"fid":"stalefid"}]}}""") // 目录里发现同名残留
            .EnqueueJson("""{"status":200,"code":0,"data":{}}""")                                    // 删除残留
            .EnqueueJson("""{"status":200,"code":0,"data":{"task_id":"t1"}}""")                      // 转存任务
            .EnqueueJson("""{"status":200,"code":0,"data":{"status":2}}""")                          // 任务完成
            .EnqueueJson("""{"status":200,"code":0,"data":{"list":[{"file_name":"mrs.zip","dir":false,"fid":"newfid"}]}}"""); // 找到新文件
        var api = CreateQuarkApi(handler);

        var result = await api.TransferAsync(new TransferRequest(
            PanPlatform.Quark, "pwd1", "https://pan.quark.cn/s/pwd1", "", "sharefid", "tok", "mrs.zip", 100, "/AppUpdater"));

        result.Success.Should().BeTrue();
        result.FileId.Should().Be("newfid");
        handler.RequestUris.Should().Contain(u => u.Contains("/file/delete"));
    }

    private QuarkPanApi CreateQuarkApi(TestHttpHandler handler)
    {
        _paths.EnsureCreated();
        var sessions = new PanSessionStore(new SecretStore(_paths));
        sessions.SaveCookie(PanPlatform.Quark, "k=v; foo=bar");
        return new QuarkPanApi(new HttpClient(handler), sessions, NullLogger<QuarkPanApi>.Instance);
    }
}
