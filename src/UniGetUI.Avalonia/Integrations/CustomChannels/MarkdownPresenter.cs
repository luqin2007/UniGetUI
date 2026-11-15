using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UniGetUI.Extensions.CustomChannels.Pan;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 轻量 Markdown 渲染：标题、列表、引用、分隔线、代码块、GFM 表格、
/// 可点击链接与图片、行内强调。不引入额外依赖，也不启用内嵌 HTML，
/// 避免把远端内容当可执行标记处理。
/// </summary>
internal static class MarkdownPresenter
{
    private static readonly System.Text.RegularExpressions.Regex HtmlTag =
        new(@"<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HttpClient ImageHttp = CreateImageHttp();

    private static HttpClient CreateImageHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", PanApiEndpoints.BrowserUserAgent);
        return http;
    }

    /// <summary>
    /// 远端 README 可能是整页 HTML 源码（GitHub raw 版），把成片的 HTML 标签
    /// 转成换行并剥掉后按纯文本段落渲染，避免界面上出现标签原文。
    /// </summary>
    private static string? Sanitize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        int tagCount = 0;
        foreach (var _ in HtmlTag.Matches(markdown))
        {
            tagCount++;
            if (tagCount > 5)
            {
                break;
            }
        }

        if (tagCount <= 5)
        {
            return markdown;
        }

        var text = System.Net.WebUtility.HtmlDecode(markdown);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"</?(?:html|body|div|p|br|h[1-6]|li|ul|ol|tr|table|section|header|footer)\b[^>]*>",
            "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = HtmlTag.Replace(text, string.Empty);
        // 折叠连续空行
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public static Control Build(string? markdown)
    {
        var panel = new StackPanel { Spacing = 4 };
        markdown = Sanitize(markdown);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            panel.Children.Add(new TextBlock { Text = "（无内容）", Opacity = 0.6, FontSize = 12 });
            return panel;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var code = new StringBuilder();
        bool inCode = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    panel.Children.Add(CodeBlock(code.ToString()));
                    code.Clear();
                }

                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                code.AppendLine(line);
                continue;
            }

            if (line.Length == 0)
            {
                panel.Children.Add(new Border { Height = 4 });
                continue;
            }

            if (line.Trim() is "---" or "***" or "___")
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4),
                    Background = Brushes.Gray,
                    Opacity = 0.4,
                });
                continue;
            }

            if (line.StartsWith('#'))
            {
                int level = line.TakeWhile(c => c == '#').Count();
                string text = line[level..].Trim();
                panel.Children.Add(Rich(text, 18 - Math.Min(level, 4) * 2, FontWeight.SemiBold));
                continue;
            }

            if (line.StartsWith('>'))
            {
                var quote = Rich(line.TrimStart('>', ' '), 12, FontWeight.Normal, style: FontStyle.Italic);
                quote.Opacity = 0.75;
                panel.Children.Add(quote);
                continue;
            }

            // GFM 表格：首行为表头（以 | 开头），紧跟分隔行（| --- | --- |）
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('|') &&
                i + 1 < lines.Length &&
                IsTableSeparator(lines[i + 1]))
            {
                var rows = new List<string[]> { ParseTableRow(trimmed) };
                i += 2; // 跳过表头与分隔行
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    rows.Add(ParseTableRow(lines[i]));
                    i++;
                }

                i--;
                panel.Children.Add(Table(rows));
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                panel.Children.Add(Rich("•  " + trimmed[2..], 12, FontWeight.Normal, indent: 10));
                continue;
            }

            int dot = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (dot is > 0 and <= 3 && trimmed[..dot].All(char.IsDigit))
            {
                panel.Children.Add(Rich(trimmed, 12, FontWeight.Normal, indent: 10));
                continue;
            }

            panel.Children.Add(Rich(line, 12, FontWeight.Normal));
        }

        if (inCode && code.Length > 0)
        {
            panel.Children.Add(CodeBlock(code.ToString()));
        }

        return panel;
    }

    /// <summary>GFM 分隔行：每个单元格形如 --- / :--- / :--: / ---:。</summary>
    internal static bool IsTableSeparator(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|') || trimmed.EndsWith('|'))
        {
            trimmed = trimmed.Trim('|');
        }
        else
        {
            return false;
        }

        foreach (var rawCell in trimmed.Split('|'))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(rawCell.Trim(), @"^:?-+:?$"))
            {
                return false;
            }
        }

        return trimmed.Length > 0;
    }

    /// <summary>拆分一行表格为单元格：去首尾竖线、按 | 分列、还原转义 \|。</summary>
    internal static string[] ParseTableRow(string line)
    {
        var cells = line.Trim().Trim('|').Split('|');
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = cells[i].Trim().Replace(@"\|", "|");
        }

        return cells;
    }

    /// <summary>
    /// 行内解析：行内代码 `x`、粗体 **x**、链接 [x](url)、图片 ![x](url)。
    /// 无特殊元素时返回单个可换行 TextBlock；有链接/图片时返回 WrapPanel 混排。
    /// </summary>
    private static Control Rich(
        string text,
        double size,
        FontWeight weight,
        double indent = 0,
        FontStyle? style = null)
    {
        // 快速路径：无行内元素时保持可自动换行的单 TextBlock
        if (!text.Contains('`') && !text.Contains("](") && !text.Contains("**") && !text.Contains("!["))
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = weight,
                FontStyle = style ?? FontStyle.Normal,
                Margin = new Thickness(indent, 0, 0, 0),
            };
        }

        var container = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(indent, 0, 0, 0) };
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            var block = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = weight,
            };
            if (style is { } fontStyle)
            {
                block.FontStyle = fontStyle;
            }

            block.Inlines!.Add(new Run(buffer.ToString()) { FontWeight = weight });
            container.Children.Add(block);
            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // 行内代码：`code`
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    container.Children.Add(new TextBlock
                    {
                        Text = text[(i + 1)..end],
                        FontSize = size,
                        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                        Background = new SolidColorBrush(Color.Parse("#33808080")),
                    });
                    i = end;
                    continue;
                }
            }

            // 粗体：**text**
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush();
                    container.Children.Add(new TextBlock
                    {
                        Text = text[(i + 2)..end],
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = size,
                        FontWeight = FontWeight.Bold,
                    });
                    i = end + 1;
                    continue;
                }
            }

            // 图片：![alt](url) —— 异步加载为真实图片，失败时显示说明文字
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                text.IndexOf("](", i + 1, StringComparison.Ordinal) is int imgMid and > 0)
            {
                int imgEnd = text.IndexOf(')', imgMid + 2);
                if (imgEnd > imgMid)
                {
                    Flush();
                    container.Children.Add(ImageBlock(
                        text[(imgMid + 2)..imgEnd].Trim().Trim('<', '>'),
                        text[(i + 2)..imgMid],
                        size));
                    i = imgEnd;
                    continue;
                }
            }

            // 链接：[text](url) —— 可点击，交给系统默认浏览器打开
            if (c == '[' && text.IndexOf("](", i, StringComparison.Ordinal) is int mid and > 0)
            {
                int end = text.IndexOf(')', mid + 2);
                if (end > mid)
                {
                    Flush();
                    container.Children.Add(Link(
                        text[(i + 1)..mid],
                        text[(mid + 2)..end].Trim().Trim('<', '>'),
                        size));
                    i = end;
                    continue;
                }
            }

            buffer.Append(c);
        }

        Flush();
        if (container.Children.Count == 0)
        {
            container.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap });
        }

        return container;
    }

    /// <summary>可点击的超链接：默认浏览器打开；悬停提示完整地址。</summary>
    private static Control Link(string label, string url, double size)
    {
        var button = new HyperlinkButton
        {
            Content = string.IsNullOrWhiteSpace(label) ? url : label,
            FontSize = size,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Foreground = new SolidColorBrush(Color.Parse("#4C9AFF")),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        ToolTip.SetTip(button, url);
        button.Click += (_, _) => TryOpen(url);
        return button;
    }

    private static void TryOpen(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开失败静默忽略：链接地址可从悬停提示复制
        }
    }

    /// <summary>图片：先显示占位文本，加载成功后替换为真实位图（限宽 560）。</summary>
    private static Control ImageBlock(string url, string alt, double size)
    {
        var host = new ContentControl
        {
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2),
        };
        host.Content = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(alt) ? $"图片：{url}" : $"🖼 {alt}",
            FontSize = Math.Max(11, size - 1),
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        };

        _ = LoadImageAsync(host, url, alt, size);
        return host;
    }

    private static async Task LoadImageAsync(ContentControl host, string url, string alt, double size)
    {
        try
        {
            using var response = await ImageHttp.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var bitmap = new Bitmap(new MemoryStream(bytes));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                host.Content = new Image
                {
                    Source = bitmap,
                    MaxWidth = 560,
                    Stretch = Stretch.Uniform,
                };
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                host.Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(alt) ? $"图片加载失败：{url}" : $"{alt}（图片加载失败）",
                    FontSize = Math.Max(11, size - 1),
                    Opacity = 0.65,
                    TextWrapping = TextWrapping.Wrap,
                };
            });
        }
    }

    /// <summary>GFM 表格：首行为表头（加粗 + 底色），其余行按列排布。</summary>
    private static Control Table(IReadOnlyList<string[]> rows)
    {
        int cols = rows.Count > 0 ? rows.Max(r => r.Length) : 0;
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 6) };

        for (int c = 0; c < cols; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        }

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cols; c++)
            {
                var cellText = c < rows[r].Length ? rows[r][c] : string.Empty;
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#40808080")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 4),
                    Background = r == 0 ? new SolidColorBrush(Color.Parse("#22808080")) : null,
                    Child = Rich(cellText, 12, r == 0 ? FontWeight.SemiBold : FontWeight.Normal),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return grid;
    }

    private static Control CodeBlock(string code)
    {
        var text = new SelectableTextBlock
        {
            Text = code.TrimEnd(),
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.NoWrap,
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#22808080")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = text,
            },
        };
    }
}
