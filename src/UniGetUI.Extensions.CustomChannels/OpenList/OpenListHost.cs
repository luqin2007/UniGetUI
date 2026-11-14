using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Infrastructure;
using Microsoft.Extensions.Logging;

namespace UniGetUI.Extensions.CustomChannels.OpenList;

/// <summary>
/// 本地 OpenList 实例托管：优先复用用户已安装的 OpenList 可执行文件，
/// 只有在本机找不到可用版本时才按需下载官方 Windows 二进制（不随本工具分发）。
/// 无论使用哪个二进制，运行时数据目录始终位于程序目录内。
/// </summary>
public sealed class OpenListHost
{
    private const string ReleaseApi = "https://api.github.com/repos/OpenListTeam/OpenList/releases/latest";
    private const string OfficialReleasesPage = "https://github.com/OpenListTeam/OpenList/releases";

    private readonly AppPaths _paths;
    private readonly OpenListConfig _config;
    private readonly DownloadConfig _downloadConfig;
    private readonly ILogger<OpenListHost> _logger;
    private Process? _process;
    private OpenListBinary? _resolved;
    private IReadOnlyList<OpenListBinary>? _candidates;

    public OpenListHost(
        AppPaths paths,
        OpenListConfig config,
        DownloadConfig downloadConfig,
        ILogger<OpenListHost> logger)
    {
        _paths = paths;
        _config = config;
        _downloadConfig = downloadConfig;
        _logger = logger;
    }

    /// <summary>程序目录内托管的安装目录。</summary>
    public string InstallDirectory => Path.Combine(_paths.Tools, "openlist");

    /// <summary>程序目录内托管的可执行文件（下载或手动放置的目标位置）。</summary>
    public string ManagedBinaryPath => Path.Combine(InstallDirectory, "openlist.exe");

    public string DataDirectory => Path.Combine(InstallDirectory, "data");

    /// <summary>当前实际使用的可执行文件；未验证时给出检测到的首选路径。</summary>
    public string BinaryPath => _resolved?.Path ?? DetectedBinary?.Path ?? ManagedBinaryPath;

    /// <summary>已通过启动验证的可执行文件；未验证前为 null。</summary>
    public OpenListBinary? ResolvedBinary => _resolved;

    /// <summary>仅做路径探测得到的首选候选（不保证可运行），用于界面展示。</summary>
    public OpenListBinary? DetectedBinary => _resolved ?? Candidates.FirstOrDefault();

    /// <summary>是否复用了本机安装（而非程序目录内的托管副本）。</summary>
    public bool UsesInstalledBinary => DetectedBinary is { Source: not OpenListBinarySource.Bundled };

    public string BinarySourceText => DetectedBinary?.SourceText ?? "未检测到";

    public string AdminPassword { get; private set; } = string.Empty;

    public bool IsInstalled => _resolved is not null || File.Exists(ManagedBinaryPath);

    /// <summary>配置是否允许在找不到本机版本时自动下载二进制。</summary>
    public bool AutoDownloadAvailable => _config.AutoDownloadBinary;

    /// <summary>按优先级排列的本机候选可执行文件（不含运行验证，结果缓存）。</summary>
    public IReadOnlyList<OpenListBinary> Candidates => _candidates ??= OpenListLocator.Locate(_config, _paths);

    /// <summary>用户修改安装路径或新装 OpenList 后重新探测。</summary>
    public void RefreshCandidates()
    {
        _candidates = null;
        _resolved = null;
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// 确保有一个可运行的 OpenList：先验证本机候选，再回落到托管副本，
    /// 最后才在允许时下载官方二进制。
    /// </summary>
    public async Task EnsureBinaryAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        if (_resolved is not null)
        {
            return;
        }

        var candidates = Candidates;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"检测到 {candidate.SourceText} 的 OpenList，正在验证…");

            if (await CanRunAsync(candidate.Path, ct).ConfigureAwait(false))
            {
                _resolved = candidate;
                _logger.LogInformation(
                    "使用 OpenList 可执行文件：{Path}（来源：{Source}）", candidate.Path, candidate.SourceText);
                progress?.Report($"使用{candidate.SourceText}的 OpenList");
                return;
            }

            _logger.LogWarning("OpenList 候选无法运行，已跳过：{Path}", candidate.Path);
        }

        if (!_config.AutoDownloadBinary)
        {
            throw new InvalidOperationException(BuildMissingMessage());
        }

        progress?.Report("未检测到可用的本机 OpenList，准备下载官方二进制…");
        await DownloadBinaryAsync(progress, ct).ConfigureAwait(false);
        _resolved = new OpenListBinary(ManagedBinaryPath, OpenListBinarySource.Bundled);
    }

    /// <summary>启动本地实例，返回监听端口。</summary>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        if (IsRunning)
        {
            return _config.LocalPort;
        }

        if (_resolved is null && File.Exists(ManagedBinaryPath))
        {
            _resolved = new OpenListBinary(ManagedBinaryPath, OpenListBinarySource.Bundled);
        }

        var binary = _resolved ?? throw new InvalidOperationException(BuildMissingMessage());

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(Path.Combine(InstallDirectory, "temp"));
        EnsureConfigFile();

        AdminPassword = string.IsNullOrWhiteSpace(AdminPassword) ? GeneratePassword() : AdminPassword;
        await SetAdminPasswordAsync(binary.Path, AdminPassword, ct).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo(binary.Path)
        {
            // 工作目录固定为程序目录内的数据目录：既可写，也不会污染本机安装目录
            WorkingDirectory = DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(DataDirectory);

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogDebug("openlist: {Line}", e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogWarning("openlist: {Line}", e.Data);
            }
        };

        if (!_process.Start())
        {
            throw new InvalidOperationException("无法启动 OpenList 进程。");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _logger.LogInformation(
            "OpenList 启动：{Path}（来源：{Source}），端口 {Port}",
            binary.Path,
            binary.SourceText,
            _config.LocalPort);

        if (!await WaitUntilReadyAsync(_config.LocalPort, ct).ConfigureAwait(false))
        {
            Stop();
            throw new InvalidOperationException(
                $"OpenList 未能在端口 {_config.LocalPort} 上就绪。请检查端口占用，或在设置中改用外部实例。");
        }

        return _config.LocalPort;
    }

    public void Stop()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug("停止 OpenList 时出现异常：{Message}", ex.Message);
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    /// <summary>下载并解压官方二进制到程序目录 tools/openlist。</summary>
    private async Task DownloadBinaryAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(ManagedBinaryPath))
        {
            return;
        }

        Directory.CreateDirectory(InstallDirectory);
        progress?.Report("正在查询 OpenList 最新版本…");

        using var http = UpdaterHttp.Create(_downloadConfig, UpdaterHttp.AppUserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await http.GetAsync(ReleaseApi, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载 OpenList 失败：GitHub 接口返回 HTTP {(int)response.StatusCode}。请手动下载后放入 {InstallDirectory}（{OfficialReleasesPage}）。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var tag = document.RootElement.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;

        string? downloadUrl = null;
        var fileName = "openlist.zip";

        if (document.RootElement.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var isWindows = name.Contains("windows", StringComparison.OrdinalIgnoreCase);
                var isAmd64 = name.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
                              name.Contains("x86_64", StringComparison.OrdinalIgnoreCase);
                if (!isWindows || !isAmd64)
                {
                    continue;
                }

                downloadUrl = asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null;
                fileName = name;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException(
                $"未能从 GitHub Release 中找到 Windows amd64 版本的 OpenList，请手动下载后放入 {InstallDirectory}（{OfficialReleasesPage}）。");
        }

        progress?.Report($"正在下载 OpenList {tag} …");

        var downloadPath = Path.Combine(InstallDirectory, fileName);
        using (var fileStream = File.Create(downloadPath))
        using (var downloadStream = await http.GetStreamAsync(downloadUrl, ct).ConfigureAwait(false))
        {
            await downloadStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
        }

        progress?.Report("正在解压 OpenList …");

        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(InstallDirectory, "_extract");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            ZipFile.ExtractToDirectory(downloadPath, extractDir, overwriteFiles: true);
            var exe = Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("OpenList 压缩包中未找到可执行文件。");
            File.Move(exe, ManagedBinaryPath, overwrite: true);
            Directory.Delete(extractDir, recursive: true);
        }
        else
        {
            File.Move(downloadPath, ManagedBinaryPath, overwrite: true);
        }

        JsonFileStore.TryDelete(downloadPath);
        _candidates = null;
        _logger.LogInformation("OpenList 已安装到 {Path}", ManagedBinaryPath);
        progress?.Report("OpenList 安装完成");
    }

    /// <summary>用 version / --help 验证候选是否可以正常作为 CLI 运行。</summary>
    private async Task<bool> CanRunAsync(string executablePath, CancellationToken ct)
    {
        foreach (var argument in new[] { "version", "--help" })
        {
            Process? process = null;
            try
            {
                var startInfo = new ProcessStartInfo(executablePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? _paths.Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                startInfo.ArgumentList.Add(argument);

                process = Process.Start(startInfo);
                if (process is null)
                {
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));

                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var text = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);

                if (process.ExitCode == 0 &&
                    (text.Contains("openlist", StringComparison.OrdinalIgnoreCase) ||
                     text.Contains("alist", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("验证 OpenList 候选超时：{Path}", executablePath);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                           or InvalidOperationException
                                           or IOException)
            {
                _logger.LogDebug("验证 OpenList 候选失败：{Path} - {Message}", executablePath, ex.Message);
            }
            finally
            {
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException
                                                   or System.ComponentModel.Win32Exception
                                                   or NotSupportedException)
                    {
                        // 进程可能已退出
                    }

                    process.Dispose();
                }
            }
        }

        return false;
    }

    private string BuildMissingMessage() =>
        "未检测到可用的 OpenList。" +
        $"\r\n 1. 安装 OpenList 并加入系统 PATH，或在设置中填写 openlist.exe 路径；" +
        $"\r\n 2. 手动下载后放到 {InstallDirectory}（官方地址：{OfficialReleasesPage}）；" +
        "\r\n 3. 在设置中开启「允许自动下载二进制」，由本工具下载到程序目录。";

    private async Task SetAdminPasswordAsync(string executablePath, string password, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            WorkingDirectory = DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("admin");
        startInfo.ArgumentList.Add("set");
        startInfo.ArgumentList.Add(password);
        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(DataDirectory);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("无法执行 OpenList admin 命令。");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("OpenList 管理员口令设置超时，将继续尝试启动。");
        }
    }

    private void EnsureConfigFile()
    {
        var configPath = Path.Combine(DataDirectory, "config.json");
        var tempDir = Path.Combine(InstallDirectory, "temp");

        JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["scheme"] is not JsonObject scheme)
        {
            scheme = new JsonObject();
            root["scheme"] = scheme;
        }

        scheme["http_port"] = _config.LocalPort;
        // 强制把 OpenList 的临时目录放在程序目录内，避免写入系统临时目录
        root["temp_dir"] = tempDir;

        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<bool> WaitUntilReadyAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/ping", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (text.Contains("pong", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }

            await Task.Delay(800, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static string GeneratePassword()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(18);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}
