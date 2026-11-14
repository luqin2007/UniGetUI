using System.IO.Compression;
using System.Text;
using UniGetUI.Extensions.CustomChannels.Configuration;
using UniGetUI.Extensions.CustomChannels.Packages;
using FluentAssertions;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace UniGetUI.Extensions.CustomChannels.Tests;

public sealed class ArchiveExtractorTests : IDisposable
{
    private readonly string _root;
    private readonly ArchiveExtractor _extractor = new();

    public ArchiveExtractorTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "zip-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task 普通压缩包应使用快速路径解压()
    {
        var zip = Path.Combine(_root, "plain.zip");
        CreatePlainZip(zip, ("app/readme.txt", "hello"));

        var destination = Path.Combine(_root, "out-plain");
        var result = await _extractor.ExtractAsync(
            zip, destination, new ArchiveConfig(), null, "plain.zip");

        result.PasswordUsed.Should().BeFalse();
        File.ReadAllText(Path.Combine(destination, "app", "readme.txt")).Should().Be("hello");
    }

    [Fact]
    public async Task 加密压缩包应使用候选密码解压()
    {
        var zip = Path.Combine(_root, "secret.zip");
        CreateEncryptedZip(zip, "s3cret", ("config/settings.json", "{\"ok\":true}"));

        var destination = Path.Combine(_root, "out-secret");
        var result = await _extractor.ExtractAsync(
            zip,
            destination,
            new ArchiveConfig { Passwords = ["wrong", "s3cret"], PromptForPassword = false },
            null,
            "secret.zip");

        result.PasswordUsed.Should().BeTrue();
        result.Password.Should().Be("s3cret");
        File.ReadAllText(Path.Combine(destination, "config", "settings.json"))
            .Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task 密码错误应抛出加密异常()
    {
        var zip = Path.Combine(_root, "secret2.zip");
        CreateEncryptedZip(zip, "right", ("a.txt", "content"));

        var act = () => _extractor.ExtractAsync(
            zip,
            Path.Combine(_root, "out-bad"),
            new ArchiveConfig { Passwords = ["wrong"], PromptForPassword = false },
            null,
            "secret2.zip");

        await act.Should().ThrowAsync<ArchivePasswordException>();
    }

    [Fact]
    public async Task 路径穿越条目应被拒绝()
    {
        var zip = Path.Combine(_root, "slip.zip");
        CreatePlainZip(zip, ("../evil.txt", "pwned"));

        var act = () => _extractor.ExtractAsync(
            zip, Path.Combine(_root, "out-slip"), new ArchiveConfig(), null, "slip.zip");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/windows/system32/evil.dll")]
    public void SafeCombine_应拒绝越界路径(string entry)
    {
        var act = () => ArchiveExtractor.SafeCombine(_root, entry);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void 包类型识别应区分压缩包与安装程序()
    {
        var zip = Path.Combine(_root, "x.zip");
        CreatePlainZip(zip, ("a.txt", "a"));

        PackageDetector.Detect(zip).Should().Be(InstallKind.ZipOverwrite);
        PackageDetector.Detect(Path.Combine(_root, "setup.msi")).Should().Be(InstallKind.Msi);
        PackageDetector.Detect(Path.Combine(_root, "setup.exe")).Should().Be(InstallKind.Exe);
        PackageDetector.Detect(Path.Combine(_root, "app.dll")).Should().Be(InstallKind.SingleFile);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void CreatePlainZip(string path, params (string Name, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }

    private static void CreateEncryptedZip(string path, string password, params (string Name, string Content)[] entries)
    {
        using var fileStream = File.Create(path);
        using var zipStream = new ZipOutputStream(fileStream) { Password = password };

        foreach (var (name, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var entry = new ZipEntry(name) { DateTime = DateTime.Now, Size = bytes.Length };
            zipStream.PutNextEntry(entry);
            zipStream.Write(bytes, 0, bytes.Length);
            zipStream.CloseEntry();
        }

        zipStream.Finish();
    }
}
