using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.CustomChannels.Infrastructure;

/// <summary>日志脱敏：Cookie、令牌与密码不落盘。</summary>
public static partial class SensitiveDataRedactor
{
    private static readonly string[] SensitiveKeys =
    [
        "BDUSS", "STOKEN", "BDCLND", "__puus", "__pus", "bdstoken", "Authorization",
        "Cookie", "token", "access_token", "refresh_token", "pwd", "passcode", "password",
        "sekey", "randsk", "sign",
    ];

    [GeneratedRegex(
        @"(?<key>BDUSS|STOKEN|BDCLND|__puus|__pus|bdstoken|Authorization|Cookie|access_token|refresh_token|token|pwd|passcode|password|sekey|randsk|sign)\s*[=:]\s*(?<value>[^&;,\s""'}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"""(?<key>[^""]*(?:token|password|pwd|secret|cookie)[^""]*)""\s*:\s*""(?<value>[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex JsonRegex();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = KeyValueRegex().Replace(text, m => $"{m.Groups["key"].Value}=***");
        result = JsonRegex().Replace(result, m => $"\"{m.Groups["key"].Value}\":\"***\"");
        return result;
    }

    public static bool IsSensitiveKey(string key) =>
        SensitiveKeys.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase));
}
