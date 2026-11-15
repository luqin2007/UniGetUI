using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Pan;

public sealed record BaiduShareSession(string SUrl, string ShareId, string Uk, string SeKey);

public sealed record BaiduShareEntry(string FsId, string Name, long Size, bool IsDir, string Path);

/// <summary>
/// 百度网盘接口封装：分享校验 → 目录列举 → 转存 → 取直链 → 删除转存。
/// 全部为普通 HTTP 调用，不依赖百度网盘客户端。
/// </summary>
public sealed partial class BaiduPanApi(
    HttpClient http,
    PanSessionStore sessions,
    ILogger<BaiduPanApi> logger) : IPanTransferService
{
    private readonly ConcurrentDictionary<string, BaiduShareSession> _shareSessions = new();

    public string Platform => PanPlatform.Baidu;

    [GeneratedRegex(@"/s/([A-Za-z0-9_\-]+)", RegexOptions.Compiled)]
    private static partial Regex ShareUrlRegex();

    [GeneratedRegex(@"[?&]pwd=([A-Za-z0-9]+)", RegexOptions.Compiled)]
    private static partial Regex PwdRegex();

    /// <summary>解析分享链接中的 surl 与提取码。</summary>
    public static (string SUrl, string? Pwd) ParseShareUrl(string shareUrl)
    {
        var match = ShareUrlRegex().Match(shareUrl);
        if (!match.Success)
        {
            throw new PanApiException(
                $"无法从链接中识别分享 ID：{Infrastructure.UpdaterHttp.RedactUrl(new Uri(shareUrl))}。" +
                "支持 https://pan.baidu.com/s/xxxx 形式。");
        }

        var pwdMatch = PwdRegex().Match(shareUrl);
        return (match.Groups[1].Value, pwdMatch.Success ? pwdMatch.Groups[1].Value : null);
    }

    public async Task<BaiduShareSession> VerifyShareAsync(string shareUrl, string? pwd, CancellationToken ct = default)
    {
        var (surl, urlPwd) = ParseShareUrl(shareUrl);
        var password = string.IsNullOrWhiteSpace(pwd) ? urlPwd ?? string.Empty : pwd;
        var cookie = RequireCookie("尚未登录百度网盘，请先在「网盘登录」中完成登录。");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var content = PanHttp.Form(
        [
            new("pwd", password),
            new("vcode", string.Empty),
            new("vcode_str", string.Empty),
        ]);

        using var request = PanHttp.Create(
            HttpMethod.Post,
            PanApiEndpoints.Baidu.Verify(surl, timestamp),
            cookie,
            PanApiEndpoints.Baidu.Referer,
            content);
        PanHttp.ApplyAccept(request);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "百度网盘分享校验", ct).ConfigureAwait(false);
        var root = document.RootElement;

        var errno = GetInt(root, "errno") ?? -1;
        if (errno != 0)
        {
            var message = errno switch
            {
                2 => "提取码错误。",
                -9 => "分享已过期或已被取消。",
                -62 => "访问过于频繁，请稍后再试。",
                _ => $"校验失败（errno={errno}）{GetString(root, "err_msg")}",
            };
            throw new PanApiException("百度网盘" + message, PanHttp.Truncate(root.ToString()));
        }

        var session = new BaiduShareSession(
            surl,
            GetString(root, "shareid") ?? string.Empty,
            GetString(root, "uk") ?? string.Empty,
            GetString(root, "randsk") ?? string.Empty);

        _shareSessions[session.ShareId] = session;
        logger.LogInformation("百度网盘分享校验成功：shareId={ShareId}", session.ShareId);
        return session;
    }

    public async Task<IReadOnlyList<BaiduShareEntry>> ListShareAsync(
        BaiduShareSession session,
        string dir,
        CancellationToken ct = default)
    {
        var cookie = CookieParser.With(RequireCookie(null), "BDCLND", session.SeKey);
        using var request = PanHttp.Create(
            HttpMethod.Get,
            PanApiEndpoints.Baidu.ListShare(session.SUrl, dir),
            cookie,
            PanApiEndpoints.Baidu.Referer);
        PanHttp.ApplyAccept(request);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "百度网盘目录列举", ct).ConfigureAwait(false);
        var root = document.RootElement;

        var errno = GetInt(root, "errno") ?? -1;
        if (errno != 0)
        {
            throw new PanApiException(
                $"百度网盘目录列举失败（errno={errno}）{GetString(root, "err_msg")}",
                PanHttp.Truncate(root.ToString()));
        }

        var entries = new List<BaiduShareEntry>();
        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var name = GetString(item, "server_filename") ?? string.Empty;
                var fsId = GetString(item, "fs_id") ?? string.Empty;
                if (name.Length == 0 || fsId.Length == 0)
                {
                    continue;
                }

                entries.Add(new BaiduShareEntry(
                    fsId,
                    name,
                    GetLong(item, "size") ?? 0,
                    GetInt(item, "isdir") == 1,
                    GetString(item, "path") ?? name));
            }
        }

        return entries;
    }

    public async Task<TransferResult> TransferAsync(TransferRequest request, CancellationToken ct = default)
    {
        if (!_shareSessions.TryGetValue(request.ShareId, out var session))
        {
            throw new PanApiException("转存前必须先校验分享链接（内部状态丢失）。");
        }

        var cookie = RequireCookie("尚未登录百度网盘。");
        using var content = PanHttp.Form(
        [
            new("fsidlist", $"[{request.FileId}]"),
            new("path", request.TransferPath),
        ]);

        using var httpRequest = PanHttp.Create(
            HttpMethod.Post,
            PanApiEndpoints.Baidu.Transfer(session.ShareId, session.Uk, session.SeKey),
            CookieParser.With(cookie, "BDCLND", session.SeKey),
            PanApiEndpoints.Baidu.Referer,
            content);
        PanHttp.ApplyAccept(httpRequest);

        using var response = await http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "百度网盘转存", ct).ConfigureAwait(false);
        var root = document.RootElement;

        var errno = GetInt(root, "errno") ?? -1;
        if (errno != 0)
        {
            var message = errno switch
            {
                12 => "目标目录已存在同名文件。",
                -33 => "转存文件数量超出限制。",
                -6 => "登录状态无效，请重新登录百度网盘。",
                _ => $"转存失败（errno={errno}）{GetString(root, "show_msg")}",
            };
            return new TransferResult(false, null, null, "百度网盘" + message);
        }

        string? newFsId = null;
        if (root.TryGetProperty("extra", out var extra) &&
            extra.TryGetProperty("list", out var list) &&
            list.ValueKind == JsonValueKind.Array &&
            list.GetArrayLength() > 0)
        {
            newFsId = GetString(list[0], "to");
        }

        logger.LogInformation("百度网盘转存完成：{Name} → {Path}", request.FileName, request.TransferPath);
        return new TransferResult(true, newFsId, request.TransferPath, "转存成功");
    }

    /// <summary>从自己的网盘中获取直链（先转存，再从目录列表读取 dlink）。</summary>
    public async Task<string?> GetDownloadUrlAsync(string transferPath, string fileName, CancellationToken ct = default)
    {
        var cookie = RequireCookie("尚未登录百度网盘。");
        using var request = PanHttp.Create(
            HttpMethod.Get,
            PanApiEndpoints.Baidu.ListOwn(transferPath),
            cookie,
            PanApiEndpoints.Baidu.Referer);
        PanHttp.ApplyAccept(request);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "百度网盘取链", ct).ConfigureAwait(false);
        var root = document.RootElement;

        var errno = GetInt(root, "errno") ?? -1;
        if (errno != 0)
        {
            throw new PanApiException(
                $"百度网盘取链失败（errno={errno}）{GetString(root, "err_msg")}，可能需要重新登录。",
                PanHttp.Truncate(root.ToString()));
        }

        if (!root.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sign = GetString(root, "sign");
        var timestamp = GetString(root, "timestamp");

        foreach (var item in list.EnumerateArray())
        {
            var name = GetString(item, "server_filename");
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dlink = GetString(item, "dlink");
            if (!string.IsNullOrWhiteSpace(dlink))
            {
                var separator = dlink.Contains('?') ? "&" : "?";
                return $"{dlink}{separator}filename={Uri.EscapeDataString(fileName)}";
            }

            var fsId = GetString(item, "fs_id");
            if (!string.IsNullOrWhiteSpace(fsId) && !string.IsNullOrWhiteSpace(sign) && !string.IsNullOrWhiteSpace(timestamp))
            {
                return $"{PanApiEndpoints.Baidu.Origin}/api/download?sign={sign}&timestamp={timestamp}" +
                       $"&fidlist=%5B{fsId}%5D&type=dlink&channel=chunlei&web=1&app_id={PanApiEndpoints.Baidu.AppId}&clienttype=0";
            }
        }

        return null;
    }

    public async Task<bool> DeleteAsync(TransferRecord record, CancellationToken ct = default)
    {
        var cookie = RequireCookie("尚未登录百度网盘。");
        var bdstoken = await GetBdstokenAsync(cookie, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bdstoken))
        {
            logger.LogWarning("百度网盘删除失败：未取得 bdstoken");
            return false;
        }

        var path = record.Path ?? string.Empty;
        if (path.Length == 0)
        {
            return false;
        }

        using var content = PanHttp.Form([new("filelist", $"[\"{path}\"]")]);
        using var request = PanHttp.Create(
            HttpMethod.Post,
            PanApiEndpoints.Baidu.FileManager(bdstoken),
            cookie,
            PanApiEndpoints.Baidu.Referer,
            content);
        PanHttp.ApplyAccept(request);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "百度网盘删除", ct).ConfigureAwait(false);
        var errno = GetInt(document.RootElement, "errno") ?? -1;
        if (errno != 0)
        {
            logger.LogWarning("百度网盘删除转存文件失败：errno={Errno}", errno);
            return false;
        }

        logger.LogInformation("已删除百度网盘转存文件：{Path}", path);
        return true;
    }

    /// <summary>
    /// 校验当前登录会话是否有效（能否取得 bdstoken），
    /// 未登录或失效时抛出 <see cref="PanApiException"/>。
    /// </summary>
    public async Task ValidateSessionAsync(CancellationToken ct = default)
    {
        var cookie = RequireCookie("尚未登录百度网盘。");
        var bdstoken = await GetBdstokenAsync(cookie, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bdstoken))
        {
            throw new PanApiException("百度网盘登录已失效（无法获取 bdstoken），请重新登录。");
        }
    }

    private async Task<string?> GetBdstokenAsync(string cookie, CancellationToken ct)
    {
        try
        {
            using var request = PanHttp.Create(
                HttpMethod.Get,
                PanApiEndpoints.Baidu.TemplateVariable,
                cookie,
                PanApiEndpoints.Baidu.Referer);
            PanHttp.ApplyAccept(request);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            using var document = await PanHttp.ReadJsonAsync(response, "百度网盘 bdstoken", ct).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("result", out var result))
            {
                return GetString(result, "bdstoken");
            }
        }
        catch (PanApiException ex)
        {
            logger.LogWarning("获取 bdstoken 失败：{Message}", ex.Message);
        }

        return null;
    }

    private string RequireCookie(string? message)
    {
        var cookie = sessions.GetCookie(PanPlatform.Baidu);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new PanApiException(message ?? "尚未登录百度网盘。");
        }

        return cookie;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
    }
}
