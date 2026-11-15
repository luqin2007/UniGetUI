using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.Pan;

public sealed record QuarkShareEntry(string Fid, string Token, string Name, long Size, bool IsDir);

/// <summary>
/// 夸克网盘接口封装：分享 token → 目录详情 → 转存到当前账号 → 取直链 → 删除转存。
/// 夸克限制分享文件必须先转存，因此转存是取链的必要步骤。
/// </summary>
public sealed partial class QuarkPanApi(
    HttpClient http,
    PanSessionStore sessions,
    ILogger<QuarkPanApi> logger) : IPanTransferService
{
    private const string RootFid = "0";

    private readonly ConcurrentDictionary<string, string> _stokens = new(StringComparer.OrdinalIgnoreCase);

    public string Platform => PanPlatform.Quark;

    [GeneratedRegex(@"/s/([A-Za-z0-9]+)", RegexOptions.Compiled)]
    private static partial Regex ShareUrlRegex();

    [GeneratedRegex(@"[?&]pwd=([A-Za-z0-9]+)", RegexOptions.Compiled)]
    private static partial Regex PwdRegex();

    public static (string PwdId, string? Pwd) ParseShareUrl(string shareUrl)
    {
        var match = ShareUrlRegex().Match(shareUrl);
        if (!match.Success)
        {
            throw new PanApiException(
                $"无法从链接中识别夸克分享 ID：{UpdaterHttp.RedactUrl(new Uri(shareUrl))}。" +
                "支持 https://pan.quark.cn/s/xxxx 形式。");
        }

        var pwdMatch = PwdRegex().Match(shareUrl);
        return (match.Groups[1].Value, pwdMatch.Success ? pwdMatch.Groups[1].Value : null);
    }

    public async Task<string> GetStokenAsync(string pwdId, string? pwd, CancellationToken ct = default)
    {
        if (_stokens.TryGetValue(pwdId, out var cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        using var document = await PostAsync(
            PanApiEndpoints.Quark.ShareToken,
            new JsonObject { ["pwd_id"] = pwdId, ["passcode"] = pwd ?? string.Empty },
            "夸克网盘分享 token",
            ct).ConfigureAwait(false);

        var data = GetData(document.RootElement, "夸克网盘分享 token");
        var stoken = GetString(data, "stoken");
        if (string.IsNullOrWhiteSpace(stoken))
        {
            throw new PanApiException("夸克网盘未返回 stoken，分享链接可能已失效或需要提取码。");
        }

        _stokens[pwdId] = stoken;
        return stoken;
    }

    public async Task<IReadOnlyList<QuarkShareEntry>> ListShareAsync(
        string pwdId,
        string stoken,
        string pdirFid = "0",
        CancellationToken ct = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            PanApiEndpoints.Quark.ShareDetail(pwdId, stoken, pdirFid));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "夸克网盘目录详情", ct).ConfigureAwait(false);

        var data = GetData(document.RootElement, "夸克网盘目录详情");
        var entries = new List<QuarkShareEntry>();

        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var fid = GetString(item, "fid");
                var name = GetString(item, "file_name");
                if (string.IsNullOrEmpty(fid) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                entries.Add(new QuarkShareEntry(
                    fid,
                    GetString(item, "share_fid_token") ?? string.Empty,
                    name,
                    GetLong(item, "size") ?? 0,
                    IsDirectory(item)));
            }
        }

        return entries;
    }

    public async Task<TransferResult> TransferAsync(TransferRequest request, CancellationToken ct = default)
    {
        var stoken = await GetStokenAsync(request.ShareId, request.Pwd, ct).ConfigureAwait(false);
        var parentFid = await EnsureFolderAsync(request.TransferPath, ct).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["fid_list"] = new JsonArray(request.FileId),
            ["fid_token_list"] = new JsonArray(request.FileToken ?? string.Empty),
            ["to_pdir_fid"] = parentFid,
            ["pwd_id"] = request.ShareId,
            ["stoken"] = stoken,
            ["pdir_fid"] = RootFid,
            ["scene"] = "link",
            ["_fetch_banner"] = 0,
            ["_fetch_share"] = 0,
            ["_fetch_total"] = 1,
            ["_sort"] = "file_type:asc,updated_at:desc",
        };

        using var document = await PostAsync(
            PanApiEndpoints.Quark.ShareSave,
            payload,
            "夸克网盘转存",
            ct).ConfigureAwait(false);

        var data = GetData(document.RootElement, "夸克网盘转存");
        var taskId = GetString(data, "task_id");
        if (string.IsNullOrEmpty(taskId))
        {
            return new TransferResult(false, null, null, "夸克网盘转存未返回任务 ID。");
        }

        if (!await WaitTaskAsync(taskId, ct).ConfigureAwait(false))
        {
            return new TransferResult(false, null, null, "夸克网盘转存任务未成功完成。");
        }

        var ownFid = await FindFileFidAsync(parentFid, request.FileName, ct).ConfigureAwait(false);
        logger.LogInformation("夸克网盘转存完成：{Name} → {Path}", request.FileName, request.TransferPath);
        return new TransferResult(true, ownFid, request.TransferPath, "转存成功");
    }

    public async Task<string?> GetDownloadUrlAsync(string fileId, CancellationToken ct = default)
    {
        using var document = await PostAsync(
            PanApiEndpoints.Quark.FileDownload,
            new JsonObject { ["fids"] = new JsonArray(fileId) },
            "夸克网盘取链",
            ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return null;
        }

        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            return GetString(data[0], "download_url");
        }

        return GetString(data, "download_url");
    }

    public async Task<bool> DeleteAsync(TransferRecord record, CancellationToken ct = default)
    {
        using var document = await PostAsync(
            PanApiEndpoints.Quark.FileDelete,
            new JsonObject
            {
                ["action_type"] = 2,
                ["filelist"] = new JsonArray(record.FileId),
                ["exclude_fids"] = new JsonArray(),
            },
            "夸克网盘删除转存",
            ct).ConfigureAwait(false);

        var status = GetInt(document.RootElement, "status") ?? 0;
        if (status != 200)
        {
            logger.LogWarning("夸克网盘删除转存失败：status={Status}", status);
            return false;
        }

        logger.LogInformation("已删除夸克网盘转存文件：{Name}", record.FileName);
        return true;
    }

    /// <summary>确保转存目录存在，返回目录 fid。</summary>
    public async Task<string> EnsureFolderAsync(string path, CancellationToken ct = default)
    {
        var segments = (path ?? "/").Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        var current = RootFid;

        foreach (var segment in segments)
        {
            var existing = await FindFileFidAsync(current, segment, ct, expectDirectory: true).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(existing))
            {
                current = existing;
                continue;
            }

            using var document = await PostAsync(
                PanApiEndpoints.Quark.FileCreate,
                new JsonObject
                {
                    ["pdir_fid"] = current,
                    ["file_name"] = segment,
                    ["dir_path"] = string.Empty,
                    ["dir_init_lock"] = false,
                },
                "夸克网盘创建目录",
                ct).ConfigureAwait(false);

            var data = GetData(document.RootElement, "夸克网盘创建目录");
            var created = GetString(data, "fid");
            if (string.IsNullOrEmpty(created))
            {
                throw new PanApiException($"无法在夸克网盘中创建目录：{path}");
            }

            current = created;
        }

        return current;
    }

    private async Task<string?> FindFileFidAsync(
        string parentFid,
        string name,
        CancellationToken ct,
        bool expectDirectory = false)
    {
        using var request = CreateRequest(HttpMethod.Get, PanApiEndpoints.Quark.FileSort(parentFid));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "夸克网盘目录浏览", ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (!string.Equals(GetString(item, "file_name"), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (expectDirectory && GetInt(item, "dir") != 1)
            {
                continue;
            }

            return GetString(item, "fid");
        }

        return null;
    }

    private async Task<bool> WaitTaskAsync(string taskId, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            using var document = await GetAsync(
                PanApiEndpoints.Quark.Task(taskId),
                "夸克网盘任务查询",
                ct).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                continue;
            }

            var status = GetInt(data, "status");
            if (status == 2)
            {
                return true;
            }

            if (status is null or < 0)
            {
                return false;
            }
        }

        logger.LogWarning("夸克网盘转存任务超时：{TaskId}", taskId);
        return false;
    }

    private async Task<JsonDocument> GetAsync(string url, string operation, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await PanHttp.ReadJsonAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 校验当前登录会话是否有效：轻量调用成员信息接口，
    /// 失效时抛出 <see cref="PanApiException"/>（含重新登录提示）。
    /// </summary>
    public async Task ValidateSessionAsync(CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, PanApiEndpoints.Quark.RecentList);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        using var document = await PanHttp.ReadJsonAsync(response, "夸克网盘会话校验", ct).ConfigureAwait(false);
        GetData(document.RootElement, "夸克网盘会话校验");
    }

    private async Task<JsonDocument> PostAsync(string url, JsonNode payload, string operation, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, url, PanHttp.Json(payload));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return await PanHttp.ReadJsonAsync(response, operation, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var cookie = sessions.GetCookie(PanPlatform.Quark);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new PanApiException("尚未登录夸克网盘，请先在「网盘登录」中完成登录。");
        }

        var request = PanHttp.Create(method, url, cookie, PanApiEndpoints.Quark.Origin + "/", content);
        PanHttp.ApplyAccept(request);
        if (content is not null)
        {
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json;charset=UTF-8");
        }

        return request;
    }

    /// <summary>校验 status/code 并返回 data 节点。</summary>
    private static JsonElement GetData(JsonElement root, string operation)
    {
        var status = GetInt(root, "status") ?? 0;
        var code = GetInt(root, "code") ?? 0;

        if (status != 200 || code != 0)
        {
            var message = GetString(root, "message") ?? "未知错误";
            var hint = status switch
            {
                401 => "（登录状态失效，请重新登录夸克网盘）",
                403 => "（分享已失效或提取码错误）",
                _ => string.Empty,
            };
            throw new PanApiException(
                $"{operation}失败：status={status}, code={code}, message={message}{hint}",
                PanHttp.Truncate(root.ToString()));
        }

        return root.TryGetProperty("data", out var data) ? data : root;
    }

    /// <summary>dir 字段不同接口可能返回布尔或数字（true/1），统一解析。</summary>
    private static bool IsDirectory(JsonElement item)
    {
        if (!item.TryGetProperty("dir", out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var n) && n == 1,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var b) ? b : false,
            _ => false,
        };
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
