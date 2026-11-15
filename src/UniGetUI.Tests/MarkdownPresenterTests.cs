using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Integrations.CustomChannels;
using Xunit;

namespace UniGetUI.Tests;

/// <summary>MarkdownPresenter：GFM 表格、链接、图片与行内元素渲染。</summary>
public sealed class MarkdownPresenterTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MarkdownPresenterTests).Assembly);

    [Fact]
    public Task GFM表格渲染为Grid且首行为表头() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build(
            "| 文件 | 说明 |\n| --- | --- |\n| a.txt | 来源链接 |\n| b.json | 游戏保存存档 |\n");

        var table = Assert.IsType<Grid>(FindChild(control, c => c is Grid g && g.RowDefinitions.Count >= 3));
        Assert.Equal(3, table.RowDefinitions.Count);
        Assert.Equal(2, table.ColumnDefinitions.Count);

        // 表头单元格加粗
        var headerCell = table.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0);
        var headerText = Assert.IsType<TextBlock>(((Border)headerCell).Child);
        Assert.Equal(FontWeight.SemiBold, headerText.FontWeight);

        // 普通行不加粗
        var bodyCell = table.Children.OfType<Border>().First(b => Grid.GetRow(b) == 1);
        var bodyText = Assert.IsType<TextBlock>(((Border)bodyCell).Child);
        Assert.NotEqual(FontWeight.SemiBold, bodyText.FontWeight);
    }, CancellationToken.None);

    [Fact]
    public Task 四个横杠的分隔行也算表格分隔行() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build(
            "| 文件 | 说明 |\n| ---- | ---- |\n| a.txt | x |\n");
        Assert.NotNull(FindChild(control, c => c is Grid g && g.RowDefinitions.Count == 2));
    }, CancellationToken.None);

    [Fact]
    public Task 普通竖线文本不误判为表格() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build(
            "| 文件 | 说明 |\n后续没有分隔行的普通文本 |\n");
        Assert.Null(FindChild(control, c => c is Grid g && g.RowDefinitions.Count >= 2 && g.ColumnDefinitions.Count >= 2));
        Assert.Contains("| 文件 |", CollectText(control));
    }, CancellationToken.None);

    [Fact]
    public Task 行内链接渲染为可点击的HyperlinkButton() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build("详见 [封面图](https://example.com/cover.png) 与说明\n");

        var link = FindChild(control, c => c is HyperlinkButton);
        Assert.NotNull(link);
        var button = (HyperlinkButton)link!;
        Assert.Equal("封面图", button.Content);
        Assert.Equal("https://example.com/cover.png", ToolTip.GetTip(button));
    }, CancellationToken.None);

    [Fact]
    public Task 一行多个链接各自独立解析() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build(
            "[刮削页面](https://a.example/1.png) 与 [游戏管理](https://b.example/2.png)\n");

        var links = CollectDescendants(control).OfType<HyperlinkButton>().ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("https://a.example/1.png", ToolTip.GetTip(links[0]));
        Assert.Equal("https://b.example/2.png", ToolTip.GetTip(links[1]));
    }, CancellationToken.None);

    [Fact]
    public Task 图片语法渲染为图片占位控件() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build("![封面图](https://example.com/cover.png)\n");

        // headless 环境不联网：应渲染出占位 ContentControl，说明文字含 alt
        var placeholder = FindChild(control, c => c is TextBlock t && (t.Text ?? "").Contains("封面图"));
        Assert.NotNull(placeholder);
    }, CancellationToken.None);

    [Fact]
    public Task 粗体与行内代码仍正常渲染() => _session.Dispatch(() =>
    {
        var control = MarkdownPresenter.Build("这是 **加粗** 与 `code` 混排\n");
        var text = CollectText(control);
        Assert.Contains("加粗", text);
        Assert.Contains("code", text);
    }, CancellationToken.None);

    private static Control? FindChild(Control root, Func<Control, bool> predicate) =>
        CollectDescendants(root).OfType<Control>().FirstOrDefault(predicate);

    private static IEnumerable<AvaloniaObject> CollectDescendants(Control root)
    {
        var queue = new Queue<Control>();
        foreach (var child in GetChildren(root))
        {
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;
            foreach (var child in GetChildren(current))
            {
                queue.Enqueue(child);
            }
        }
    }

    private static IEnumerable<Control> GetChildren(Control control) => control switch
    {
        Panel panel => panel.Children,
        ContentControl content when content.Content is Control child => [child],
        Border border when border.Child is Control child => [child],
        _ => [],
    };

    private static string CollectText(Control root) =>
        string.Concat(CollectDescendants(root)
            .OfType<TextBlock>()
            .SelectMany(t => new[] { t.Text ?? "" }.Concat(
                (t.Inlines ?? []).OfType<Run>().Select(r => r.Text ?? ""))));
}
