using FluentAssertions;
using UniGetUI.Extensions.CustomChannels.Sources;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>文件名通配符：* ? 与 [abc] 字符类；正则量词在通配符里是普通字符。</summary>
public sealed class WildcardMatcherTests
{
    [Theory]
    [InlineData("MRS助手_26.11.19.2.zip", "MRS助手*.zip")]
    [InlineData("MRS助手-26.11.19.2.zip", "MRS助手*.zip")]
    [InlineData("MRS助手-26.11.19.2.zip", "MRS助手[-_]*.zip")]
    [InlineData("MRS助手_26.11.19.2.zip", "MRS助手[-_]*.zip")]
    [InlineData("MRS助手-26.11.19.2.zip", "MRS助手[!A-Z]*.zip")]
    [InlineData("app-1.2.3.exe", "app-?.?.?.exe")]
    [InlineData("APP-1.2.3.EXE", "app-?.?.?.exe")]
    public void 通配符应匹配文件名(string name, string pattern) =>
        WildcardMatcher.IsMatch(name, pattern).Should().BeTrue();

    [Theory]
    [InlineData("MRS助手-26.11.19.2.zip", "MRS助手_*.zip")]
    [InlineData("MRS助手-26.11.19.2.zip", "MRS助手[-_]+*.zip")]
    [InlineData("app-1.2.3.exe", "app-?.?.exe")]
    public void 不匹配时应返回false(string name, string pattern) =>
        WildcardMatcher.IsMatch(name, pattern).Should().BeFalse();

    [Fact]
    public void 非法字符类按普通字符处理()
    {
        WildcardMatcher.IsMatch("a[z-a]b", "a[z-a]b").Should().BeTrue();
        WildcardMatcher.IsMatch("a[b", "a[b").Should().BeTrue();
    }
}
