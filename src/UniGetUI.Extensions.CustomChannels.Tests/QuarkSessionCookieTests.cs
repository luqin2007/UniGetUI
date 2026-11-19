using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>
/// 夸克会话令牌 __puus 会随接口响应轮换，直链 CDN 只认最新值；
/// 不把 Set-Cookie 写回会话就会出现「取链成功、下载 412」。
/// </summary>
public sealed class QuarkSessionCookieTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(
        Path.GetTempPath(), "cc-cookie-test-" + Guid.NewGuid().ToString("N")));

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
    public async Task 取链响应里的SetCookie应写回会话并保留其余键()
    {
        var handler = new TestHttpHandler().Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":200,"code":0,"data":[{"download_url":"https://dl.example/a.zip"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TryAddWithoutValidation(
                "Set-Cookie", "__puus=fresh-token; Max-Age=86400; Domain=quark.cn; Path=/");
            return response;
        });
        var (api, sessions) = CreateQuarkApi(handler);

        var url = await api.GetDownloadUrlAsync("fid");

        url.Should().Be("https://dl.example/a.zip");
        sessions.GetCookieValue(PanPlatform.Quark, "__puus").Should().Be("fresh-token");
        sessions.GetCookieValue(PanPlatform.Quark, "foo").Should().Be("bar");
        handler.UserAgentHeaders.Should().ContainSingle(ua => ua!.Contains("quark-cloud-drive"));
    }

    [Fact]
    public void 空值的SetCookie不覆盖已有会话()
    {
        var (_, sessions) = CreateQuarkApi(new TestHttpHandler());

        var changed = sessions.MergeSetCookies(PanPlatform.Quark, ["__puus=; Max-Age=0; Path=/"]);

        changed.Should().BeFalse();
        sessions.GetCookieValue(PanPlatform.Quark, "__puus").Should().Be("old-token");
    }

    [Fact]
    public void 相同的SetCookie不重复写盘()
    {
        var (_, sessions) = CreateQuarkApi(new TestHttpHandler());

        sessions.MergeSetCookies(PanPlatform.Quark, ["__puus=same-token; Path=/"]).Should().BeTrue();
        sessions.MergeSetCookies(PanPlatform.Quark, ["__puus=same-token; Path=/"]).Should().BeFalse();
    }

    private (QuarkPanApi Api, PanSessionStore Sessions) CreateQuarkApi(TestHttpHandler handler)
    {
        _paths.EnsureCreated();
        var sessions = new PanSessionStore(new SecretStore(_paths));
        sessions.SaveCookie(PanPlatform.Quark, "k=v; foo=bar; __puus=old-token");
        var api = new QuarkPanApi(new HttpClient(handler), sessions, NullLogger<QuarkPanApi>.Instance);
        return (api, sessions);
    }
}
