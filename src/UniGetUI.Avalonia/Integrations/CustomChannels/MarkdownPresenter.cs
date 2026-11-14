using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace UniGetUI.Avalonia.Integrations.CustomChannels;

/// <summary>
/// 轻量 Markdown 渲染：标题、列表、引用、分隔线、代码块与行内强调。
/// 不引入额外依赖，也不启用内嵌 HTML，避免把远端内容当可执行标记处理。
/// </summary>
internal static class MarkdownPresenter
{
    public static Control Build(string? markdown)
    {
        var panel = new StackPanel { Spacing = 4 };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            panel.Children.Add(new TextBlock { Text = "（无内容）", Opacity = 0.6, FontSize = 12 });
            return panel;
        }

        var code = new StringBuilder();
        bool inCode = false;

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();

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
                var quote = Rich(line.TrimStart('>', ' '), 12, FontWeight.Normal);
                quote.Opacity = 0.75;
                quote.FontStyle = FontStyle.Italic;
                panel.Children.Add(quote);
                continue;
            }

            string trimmed = line.TrimStart();
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

    private static TextBlock Rich(string text, double size, FontWeight weight, double indent = 0)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight,
            Margin = new Thickness(indent, 0, 0, 0),
        };

        var buffer = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // 行内代码：`code`
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush(block, buffer, weight);
                    block.Inlines!.Add(new Run(text[(i + 1)..end])
                    {
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
                    Flush(block, buffer, weight);
                    block.Inlines!.Add(new Run(text[(i + 2)..end]) { FontWeight = FontWeight.Bold });
                    i = end + 1;
                    continue;
                }
            }

            // 链接：[text](url) —— 只渲染文本与地址，交给系统浏览器前需要用户自行复制
            if (c == '[' && text.IndexOf("](", i, StringComparison.Ordinal) is int mid and > 0)
            {
                int end = text.IndexOf(')', mid + 2);
                if (end > mid)
                {
                    Flush(block, buffer, weight);
                    string label = text[(i + 1)..mid];
                    string url = text[(mid + 2)..end];
                    block.Inlines!.Add(new Run($"{label} ({url})")
                    {
                        Foreground = new SolidColorBrush(Color.Parse("#4C9AFF")),
                    });
                    i = end;
                    continue;
                }
            }

            buffer.Append(c);
        }

        Flush(block, buffer, weight);
        return block;
    }

    private static void Flush(TextBlock block, StringBuilder buffer, FontWeight weight)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        block.Inlines!.Add(new Run(buffer.ToString()) { FontWeight = weight });
        buffer.Clear();
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
