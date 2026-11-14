using System.Net;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.OpenList;
using UniGetUI.Extensions.CustomChannels.Pan;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class OpenListClientTests
{
    private const string LoginJson = """{"code":200,"data":{"token":"tk-1"}}""";

    private const string ListJson = """
    {
      "code": 200,
      "data": {
        "content": [
          {
            "name": "1.0.0",
            "size": 0,
            "is_dir": true,
            "modified": "2026-01-02T03:04:05Z"
          },
          {
            "name": "app-1.0.0.zip",
            "size": 2048,
            "is_dir": false,
            "modified": "2026-01-03T00:00:00Z"
          }
        ]
      }
    }
    """;

    [Fact]
    public async Task 登录后应携带token列目录()
    {
        var handler = new TestHttpHandler()
            .EnqueueJson(LoginJson)
            .EnqueueJson(ListJson);
        var client = CreateClient(handler);

        var entries = await client.ListAsync("/AppUpdater");

        entries.Should().HaveCount(2);
        entries[0].Name.Should().Be("1.0.0");
        entries[0].IsDir.Should().BeTrue();
        entries[0].Path.Should().Be("/AppUpdater/1.0.0");
        entries[1].Size.Should().Be(2048);
        entries[1].Modified.Should().NotBeNull();

        handler.RequestUris[0].Should().EndWith("/api/auth/login");
        handler.RequestUris[1].Should().EndWith("/api/fs/list");
        handler.AuthorizationHeaders[1].Should().Be("tk-1");
    }

    [Fact]
    public async Task 配置token时不应请求登录接口()
    {
        var handler = new TestHttpHandler().EnqueueJson(ListJson);
        var client = CreateClient(handler, new OpenListConfig
        {
            BaseUrl = "http://127.0.0.1:5244",
            Token = "static-token",
        });

        await client.ListAsync("/");

        handler.RequestUris.Should().ContainSingle()
            .Which.Should().EndWith("/api/fs/list");
        handler.AuthorizationHeaders[0].Should().Be("static-token");
        client.HasToken.Should().BeTrue();
    }

    [Fact]
    public async Task 取文件信息应解析直链与sha256()
    {
        const string json = """
        {
          "code": 200,
          "data": {
            "name": "app.zip",
            "size": 4096,
            "raw_url": "http://127.0.0.1:5244/d/AppUpdater/app.zip?sign=abc",
            "hash_info": { "sha256": "deadbeef" }
          }
        }
        """;

        var handler = new TestHttpHandler().EnqueueJson(json);
        var client = CreateClient(handler, new OpenListConfig
        {
            BaseUrl = "http://127.0.0.1:5244",
            Token = "static-token",
        });

        var file = await client.GetFileAsync("/AppUpdater/app.zip");

        file.Should().NotBeNull();
        file!.Name.Should().Be("app.zip");
        file.Size.Should().Be(4096);
        file.RawUrl.Should().Contain("sign=abc");
        file.Sha256.Should().Be("deadbeef");
    }

    [Fact]
    public async Task 接口返回非200代码时应抛出带错误码的异常()
    {
        var handler = new TestHttpHandler().EnqueueJson("""{"code":500,"message":"boom"}""");
        var client = CreateClient(handler, new OpenListConfig
        {
            BaseUrl = "http://127.0.0.1:5244",
            Token = "static-token",
        });

        var act = async () => await client.ListAsync("/AppUpdater");

        await act.Should().ThrowAsync<PanApiException>()
            .WithMessage("*code=500*boom*");
    }

    [Fact]
    public async Task 缺少凭据时应提示配置token或账号()
    {
        var handler = new TestHttpHandler();
        var client = CreateClient(handler, new OpenListConfig
        {
            BaseUrl = "http://127.0.0.1:5244",
            Username = null,
            Password = null,
        });

        var act = async () => await client.LoginAsync();

        await act.Should().ThrowAsync<PanApiException>()
            .WithMessage("*用户名*");
        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task 探测pong应判定为在线()
    {
        var handler = new TestHttpHandler().EnqueueText("pong");
        var client = CreateClient(handler);

        var state = await client.PingAsync();

        state.Should().Be("在线");
        handler.RequestUris[0].Should().EndWith("/ping");
    }

    private static OpenListClient CreateClient(TestHttpHandler handler, OpenListConfig? config = null) =>
        new(
            new HttpClient(handler),
            () => config ?? new OpenListConfig
            {
                BaseUrl = "http://127.0.0.1:5244",
                Username = "admin",
                Password = "pwd",
            },
            NullLogger<OpenListClient>.Instance);
}
