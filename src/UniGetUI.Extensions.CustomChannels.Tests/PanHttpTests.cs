using System.Net.Http;
using System.Text.Json.Nodes;
using UniGetUI.Extensions.CustomChannels.Pan;
using FluentAssertions;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>
/// 网盘 HTTP 基础设施的回归测试：
/// 请求体必须走 JsonNode 直写（NativeAOT 禁用反射序列化），
/// 多值 Accept 头必须能原样写入（旧实现用 MediaTypeHeaderValue.Parse 会抛异常）。
/// </summary>
public sealed class PanHttpTests
{
    [Fact]
    public void JsonContent_应按原键名直写JsonNode()
    {
        using var content = PanHttp.Json(new JsonObject
        {
            ["pwd_id"] = "abc123",
            ["passcode"] = string.Empty,
            ["_fetch_total"] = 1,
        });

        var text = content.ReadAsStringAsync().GetAwaiter().GetResult();

        text.Should().Be("""{"pwd_id":"abc123","passcode":"","_fetch_total":1}""");
        content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public void JsonContent_空载荷应输出null()
    {
        using var content = PanHttp.Json(null);

        content.ReadAsStringAsync().GetAwaiter().GetResult().Should().Be("null");
    }

    [Fact]
    public void ApplyAccept_应接受逗号分隔的多值Accept头()
    {
        using var request = new HttpRequestMessage();

        // 旧实现 MediaTypeWithQualityHeaderValue.Parse 对多值会抛 FormatException
        var act = () => PanHttp.ApplyAccept(request);

        act.Should().NotThrow();
        request.Headers.ToString().Should().Contain("application/json, text/plain, */*");
    }

    [Fact]
    public void Create_应带上Cookie与Referer()
    {
        using var request = PanHttp.Create(
            HttpMethod.Get,
            "https://pan.quark.cn/api/list",
            cookie: "a=b; c=d",
            referer: "https://pan.quark.cn/");

        request.Headers.ToString().Should().Contain("a=b; c=d");
        request.Headers.Referrer.Should().NotBeNull();
        request.Headers.ToString().Should().Contain("https://pan.quark.cn/");
    }
}
