// 「自定义渠道」NativeAOT 冒烟测试：在反射式序列化被禁用（与发布版一致）的环境里，
// 实际运行历史上出过问题的代码路径。本地或 CI 执行：
//   dotnet publish scripts/CustomChannelsSmoke -c Release -r win-x64
//   scripts/CustomChannelsSmoke/bin/Release/net10.0/win-x64/publish/UniGetUI.CustomChannels.Smoke.exe
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using UniGetUI.Extensions.CustomChannels.Pan;
using UniGetUI.Extensions.CustomChannels.Sources;

// 开发辅助：列出夸克分享的目录内容（递归一层示例）
//   dotnet run --project scripts/CustomChannelsSmoke -- list-quark <shareUrl> [子目录名]
if (args.Length >= 2 && args[0] == "list-quark")
{
    var paths = AppPaths.Default;
    var sessions = new PanSessionStore(new SecretStore(paths));
    if (!sessions.HasSession(PanPlatform.Quark))
    {
        Console.WriteLine("没有夸克网盘会话，请先在应用中登录。");
        return 2;
    }

    using var http = new HttpClient();
    var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
    var api = new QuarkPanApi(http, sessions, loggerFactory.CreateLogger<QuarkPanApi>());
    var (pwdId, pwd) = QuarkPanApi.ParseShareUrl(args[1]);
    var stoken = await api.GetStokenAsync(pwdId, pwd).ConfigureAwait(false);

    async Task ListDirAsync(string fid, string indent)
    {
        var entries = await api.ListShareAsync(pwdId, stoken, fid).ConfigureAwait(false);
        foreach (var e in entries)
        {
            Console.WriteLine($"{indent}{(e.IsDir ? "[目录]" : "[文件]")} {e.Name}  ({e.Size} B)");
            if (e.IsDir && args.Length >= 3 && args[2] == "-r")
            {
                await ListDirAsync(e.Fid, indent + "  ");
            }
        }
    }

    await ListDirAsync("0", string.Empty);
    return 0;
}

// 开发辅助：验证带目录前缀的 pathPattern 能否列出候选版本
//   dotnet run --project scripts/CustomChannelsSmoke -- check-quark <shareUrl> <pathPattern>
if (args.Length >= 3 && args[0] == "check-quark")
{
    var paths = AppPaths.Default;
    var sessions = new PanSessionStore(new SecretStore(paths));
    if (!sessions.HasSession(PanPlatform.Quark))
    {
        Console.WriteLine("没有夸克网盘会话，请先在应用中登录。");
        return 2;
    }

    using var http = new HttpClient();
    var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
    var api = new QuarkPanApi(http, sessions, loggerFactory.CreateLogger<QuarkPanApi>());
    var transferManager = new TransferManager(paths, new TransferConfig(), loggerFactory.CreateLogger<TransferManager>());
    var source = new QuarkPanSource(
        new SourceConfig { Type = SourceType.QuarkPan, ShareUrl = args[1], PathPattern = args[2] },
        api,
        sessions,
        transferManager,
        new TransferConfig(),
        loggerFactory.CreateLogger<QuarkPanSource>());

    try
    {
        var versions = await source.ListVersionsAsync(new VersionQuery(MaxVersions: 5)).ConfigureAwait(false);
        foreach (var v in versions)
        {
            Console.WriteLine($"版本 {v.DisplayVersion} ← {string.Join(", ", v.Assets.Select(a => a.Name))}");
        }

        if (versions.Count == 0)
        {
            Console.WriteLine("没有匹配到任何版本");
            return 1;
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"检查失败：{ex.Message}");
        return 1;
    }
}

int failures = 0;
string tempRoot = Path.Join(Path.GetTempPath(), "cc-smoke-" + Guid.NewGuid().ToString("N"));

void Check(string name, Action run)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

// 环境自检：冒烟程序必须与发布版一样禁用了反射序列化，否则覆盖不了真实问题
Check("smoke-env: reflection serialization disabled", () =>
{
    try
    {
        _ = JsonSerializer.Serialize((object)new { a = 1 });
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new Exception("反射序列化仍然可用，冒烟环境与 NativeAOT 发布版不一致");
});

// 曾报错：Reflection-based serialization has been disabled（PanHttp.Json 走反射重载）
Check("pan-http: JsonNode payload serialized without reflection", () =>
{
    using var content = PanHttp.Json(new JsonObject
    {
        ["pwd_id"] = "abc123",
        ["passcode"] = string.Empty,
        ["_fetch_total"] = 1,
    });

    var text = content.ReadAsStringAsync().GetAwaiter().GetResult();
    if (text != """{"pwd_id":"abc123","passcode":"","_fetch_total":1}""")
    {
        throw new Exception($"unexpected body: {text}");
    }
});

// 曾报错：The format of value 'application/json, text/plain, */*' is invalid
Check("pan-http: multi-value Accept header accepted", () =>
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "https://pan.quark.cn/s/abc");
    PanHttp.ApplyAccept(request);
    if (!request.Headers.ToString().Contains("application/json, text/plain, */*"))
    {
        throw new Exception("Accept header missing: " + request.Headers);
    }
});

Check("pan-http: Create carries cookie/referer/UA", () =>
{
    using var request = PanHttp.Create(
        HttpMethod.Get, "https://pan.quark.cn/s/abc", "a=b; c=d", "https://pan.quark.cn/");
    var headers = request.Headers.ToString();
    if (!headers.Contains("a=b; c=d") || request.Headers.Referrer is null)
    {
        throw new Exception("cookie or referer missing: " + headers);
    }
});

// 配置保存/加载往返（AppJsonContext 源生成路径）
Check("config: save/load roundtrip", () =>
{
    var dir = Path.Join(tempRoot, "config");
    var paths = new AppPaths(dir);
    paths.EnsureCreated();

    var sample = ConfigLoader.LoadOrCreateSample(paths);
    ConfigLoader.Save(sample.Path, sample.Config);
    var reloaded = ConfigLoader.Load(sample.Path);

    if (reloaded.Config.Targets.Count != sample.Config.Targets.Count)
    {
        throw new Exception($"target count {reloaded.Config.Targets.Count} != {sample.Config.Targets.Count}");
    }
});

// README 索引往返（ReadmeIndexContext 源生成路径）
Check("readme-store: roundtrip", () =>
{
    var dir = Path.Join(tempRoot, "state");
    var store = new ReadmeStore(new AppPaths(dir));
    if (store.TryUpdate("k1", "deadbeef", DateTimeOffset.Now)) throw new Exception("first update must not report change");
    store.TryUpdate("k1", "feedface", DateTimeOffset.Now);
    if (store.GetHash("k1") != "feedface") throw new Exception($"hash: {store.GetHash("k1")}");
});

// 分享链接解析（含 #/list/share 片段的形式）
Check("quark: parse share url", () =>
{
    var (pwdId, pwd) = QuarkPanApi.ParseShareUrl(
        "https://pan.quark.cn/s/9a5c5c538c4a#/list/share/63f240e02e854b51b508c2181e417d83");
    if (pwdId != "9a5c5c538c4a" || pwd is not null)
    {
        throw new Exception($"parsed: {pwdId}, pwd={pwd ?? "null"}");
    }
});

try
{
    if (Directory.Exists(tempRoot))
    {
        Directory.Delete(tempRoot, recursive: true);
    }
}
catch (IOException)
{
}

if (failures > 0)
{
    Console.WriteLine($"CustomChannels AOT smoke FAILED ({failures} failure(s))");
    return 1;
}

Console.WriteLine("CustomChannels AOT smoke OK");
return 0;
