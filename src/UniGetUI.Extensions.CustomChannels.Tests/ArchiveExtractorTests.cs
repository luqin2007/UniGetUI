using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Packages;
using Xunit;

namespace UniGetUI.Extensions.CustomChannels.Tests;

/// <summary>压缩包解压：GBK 条目名解码、路径校验。</summary>
public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-extract-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public async Task GBK中文条目名压缩包正确解码()
    {
        // 复现线上故障：WinRAR/好压等中文压缩工具以 ANSI(GBK) 存储条目名
        // 且不带 UTF-8 标志，默认按 UTF-8 解码会得到乱码目录
        var zipPath = CreateGbkEntryZip("MRS助手/MRS助手.exe", "MRS助手_26.11.14.1.zip");
        var destination = Path.Combine(_root, "out");

        var extractor = new ArchiveExtractor();
        var result = await extractor.ExtractAsync(zipPath, destination, new ArchiveConfig(), null, "测试");

        result.Files.Should().Contain(Path.Combine("MRS助手", "MRS助手.exe"));
        File.Exists(Path.Combine(destination, "MRS助手", "MRS助手.exe")).Should().BeTrue();
        Directory.EnumerateDirectories(destination).Should().ContainSingle()
            .And.Subject.Should().Contain(p => p.EndsWith("MRS助手"));
    }

    [Fact]
    public async Task 带UTF8标志的中文条目名不受影响()
    {
        // ZipArchive 写入非 ASCII 名时会设置 UTF-8 标志，必须仍按 UTF-8 解码
        var zipPath = Path.Combine(_root, "utf8.zip");
        Directory.CreateDirectory(_root);
        using (var file = File.Create(zipPath))
        using (var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("应用/说明.txt").Open())
        {
            entry.Write("内容"u8.ToArray());
        }

        var extractor = new ArchiveExtractor();
        var result = await extractor.ExtractAsync(zipPath, Path.Combine(_root, "out"), new ArchiveConfig(), null, "测试");

        result.Files.Should().Contain(Path.Combine("应用", "说明.txt"));
    }

    /// <summary>用 SharpZipLib 以 GBK 码页写入条目名（不带 UTF-8 标志），模拟中文压缩工具产物。</summary>
    private string CreateGbkEntryZip(string entryName, string fileName)
    {
        Directory.CreateDirectory(_root);
        var zipPath = Path.Combine(_root, fileName);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ZipStrings.CodePage = 936;
        using var stream = new FileStream(zipPath, FileMode.Create);
        using var output = new ZipOutputStream(stream);
        var entry = new ZipEntry(entryName) { DateTime = DateTime.Now };
        output.PutNextEntry(entry);
        output.Write("MZ"u8.ToArray(), 0, 2);
        output.CloseEntry();
        output.Close();
        return zipPath;
    }
}
