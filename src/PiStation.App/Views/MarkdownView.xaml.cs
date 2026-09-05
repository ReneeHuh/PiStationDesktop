using ColorCode;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windows.ApplicationModel.DataTransfer;

namespace PiStation.App.Views;

public sealed partial class MarkdownView : UserControl
{
    private const string BodyStyleKey = "PiMarkdownBodyStyle";
    private const string CodeStyleKey = "PiMarkdownCodeTextStyle";
    private const string CompactButtonStyleKey = "PiCompactButtonStyle";
    private const string CodeBlockStyleKey = "PiMarkdownCodeBlockStyle";
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public MarkdownView()
    {
        InitializeComponent();
        AutomationProperties.SetName(this, "Markdown message");
        ActualThemeChanged += OnActualThemeChanged;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        ((MarkdownView)dependencyObject).Render(args.NewValue as string ?? string.Empty);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => Render(Text);

    private void Render(string source)
    {
        BlockPanel.Children.Clear();
        if (string.IsNullOrEmpty(source))
        {
            return;
        }

        MarkdownDocument document;
        try
        {
            document = Markdown.Parse(source, Pipeline);
        }
        catch
        {
            BlockPanel.Children.Add(CreatePlainText(source));
            return;
        }

        foreach (var block in document)
        {
            RenderBlock(block, BlockPanel, source, 0);
        }

        if (BlockPanel.Children.Count == 0)
        {
            BlockPanel.Children.Add(CreatePlainText(source));
        }
    }

    private static void RenderBlock(Markdig.Syntax.Block block, Panel parent, string source, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                parent.Children.Add(CreateRichText(
                    heading.Inline,
                    source,
                    fontSize: heading.Level switch
                    {
                        1 => 22,
                        2 => 19,
                        3 => 17,
                        4 => 15,
                        _ => 14,
                    },
                    fontWeight: FontWeights.SemiBold));
                break;
            case ParagraphBlock paragraph:
                parent.Children.Add(CreateRichText(paragraph.Inline, source));
                break;
            case FencedCodeBlock fencedCode:
                parent.Children.Add(CreateCodeBlock(
                    fencedCode.Lines.ToString() ?? string.Empty,
                    fencedCode.Info?.ToString()));
                break;
            case CodeBlock code:
                parent.Children.Add(CreateCodeBlock(code.Lines.ToString() ?? string.Empty, string.Empty));
                break;
            case QuoteBlock quote:
                parent.Children.Add(CreateQuoteBlock(quote, source, depth));
                break;
            case ListBlock list:
                parent.Children.Add(CreateListBlock(list, source, depth));
                break;
            case ThematicBreakBlock:
                parent.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = Resource<Microsoft.UI.Xaml.Media.Brush>("PiBorderBrush"),
                });
                break;
            case HtmlBlock html:
                parent.Children.Add(CreatePlainText(html.Lines.ToString() ?? SourceText(block, source)));
                break;
            case ContainerBlock container:
                foreach (var child in container)
                {
                    RenderBlock(child, parent, source, depth);
                }
                break;
            default:
                var fallback = SourceText(block, source);
                if (!string.IsNullOrEmpty(fallback))
                {
                    parent.Children.Add(CreatePlainText(fallback));
                }

                break;
        }
    }

    private static RichTextBlock CreateRichText(
        ContainerInline? content,
        string source,
        double? fontSize = null,
        Windows.UI.Text.FontWeight? fontWeight = null)
    {
        var textBlock = new RichTextBlock
        {
            Style = Resource<Style>(BodyStyleKey),
        };
        if (fontSize is not null)
        {
            textBlock.FontSize = fontSize.Value;
        }

        if (fontWeight is not null)
        {
            textBlock.FontWeight = fontWeight.Value;
        }

        var paragraph = new Paragraph();
        AppendInlines(content, paragraph.Inlines, source);
        textBlock.Blocks.Add(paragraph);
        return textBlock;
    }

    private static void AppendInlines(ContainerInline? container, InlineCollection target, string source)
    {
        for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;
                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard
                        ? new LineBreak()
                        : new Run { Text = " " });
                    break;
                case CodeInline code:
                    var codeSpan = new Span
                    {
                        FontFamily = Resource<Microsoft.UI.Xaml.Media.FontFamily>("PiMonospaceFontFamily"),
                        Foreground = Resource<Microsoft.UI.Xaml.Media.Brush>("PiAccentBrush"),
                    };
                    codeSpan.Inlines.Add(new Run { Text = code.Content });
                    target.Add(codeSpan);
                    break;
                case EmphasisInline emphasis:
                    Span emphasisSpan = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AppendInlines(emphasis, emphasisSpan.Inlines, source);
                    target.Add(emphasisSpan);
                    break;
                case LinkInline link when link.IsImage:
                    target.Add(new Run { Text = $"[Image: {InlineText(link, source)}]" });
                    break;
                case LinkInline link when TryCreateSafeUri(link.Url, out var uri):
                    var hyperlink = new Hyperlink
                    {
                        NavigateUri = uri,
                    };
                    AutomationProperties.SetName(hyperlink, $"Open link {InlineText(link, source)}");
                    AppendInlines(link, hyperlink.Inlines, source);
                    target.Add(hyperlink);
                    break;
                case LinkInline link:
                    AppendInlines(link, target, source);
                    break;
                case HtmlInline html:
                    target.Add(new Run { Text = html.Tag });
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;
                case ContainerInline nested:
                    AppendInlines(nested, target, source);
                    break;
                default:
                    var fallback = SourceText(inline, source);
                    if (!string.IsNullOrEmpty(fallback))
                    {
                        target.Add(new Run { Text = fallback });
                    }

                    break;
            }
        }
    }

    private static Border CreateQuoteBlock(QuoteBlock quote, string source, int depth)
    {
        var contents = new StackPanel { Spacing = 6 };
        foreach (var child in quote)
        {
            RenderBlock(child, contents, source, depth + 1);
        }

        return new Border
        {
            Margin = new Thickness(Math.Min(depth, 3) * 6, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            BorderBrush = Resource<Microsoft.UI.Xaml.Media.Brush>("PiBorderStrongBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Child = contents,
        };
    }

    private static StackPanel CreateListBlock(ListBlock list, string source, int depth)
    {
        var listPanel = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(Math.Min(depth, 4) * 14, 0, 0, 0),
        };
        var orderedIndex = int.TryParse(list.OrderedStart, out var parsedStart) ? parsedStart : 1;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
            {
                continue;
            }

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                MinWidth = 16,
                HorizontalTextAlignment = TextAlignment.Right,
                Style = Resource<Style>("PiBodyTextStyle"),
                Text = list.IsOrdered ? $"{orderedIndex++}{list.OrderedDelimiter}" : "•",
            });
            var itemPanel = new StackPanel { Spacing = 5 };
            Grid.SetColumn(itemPanel, 1);
            foreach (var itemBlock in item)
            {
                RenderBlock(itemBlock, itemPanel, source, depth + 1);
            }

            row.Children.Add(itemPanel);
            listPanel.Children.Add(row);
        }

        return listPanel;
    }

    private static Border CreateCodeBlock(string code, string? languageInfo)
    {
        var normalizedCode = code.TrimEnd('\r', '\n');
        var language = NormalizeLanguage(languageInfo);
        var displayLanguage = DisplayLanguage(language);

        var container = new Border
        {
            Style = Resource<Style>(CodeBlockStyleKey),
        };
        AutomationProperties.SetName(container, $"{displayLanguage} code block");
        AutomationProperties.SetAutomationId(container, "MarkdownCodeBlock");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(10, 6, 6, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var languageLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Style = Resource<Style>("PiPillTextStyle"),
            Text = displayLanguage,
        };
        AutomationProperties.SetAutomationId(languageLabel, "MarkdownCodeLanguage");
        header.Children.Add(languageLabel);

        var copyButton = new Button
        {
            Content = "Copy",
            Style = Resource<Style>(CompactButtonStyleKey),
        };
        Grid.SetColumn(copyButton, 1);
        AutomationProperties.SetAutomationId(copyButton, "MarkdownCodeCopyButton");
        AutomationProperties.SetName(copyButton, $"Copy {displayLanguage} code");
        copyButton.Click += (_, _) => CopyCode(copyButton, normalizedCode, displayLanguage);
        header.Children.Add(copyButton);
        layout.Children.Add(header);

        var codeText = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            Style = Resource<Style>(CodeStyleKey),
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetAutomationId(codeText, "MarkdownCodeText");
        AutomationProperties.SetName(codeText, normalizedCode);
        var codeParagraph = new Paragraph();
        var colorLanguage = string.IsNullOrEmpty(language) ? null : Languages.FindById(language);
        if (colorLanguage is null)
        {
            codeParagraph.Inlines.Add(new Run { Text = normalizedCode });
        }
        else
        {
            new RichTextBlockFormatter().FormatInlines(normalizedCode, colorLanguage, codeParagraph.Inlines);
        }

        codeText.Blocks.Add(codeParagraph);
        var scroller = new ScrollViewer
        {
            MaxHeight = 420,
            Padding = new Thickness(12, 10, 12, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = codeText,
        };
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);
        container.Child = layout;
        return container;
    }

    private static void CopyCode(Button button, string code, string language)
    {
        var package = new DataPackage();
        package.SetText(code);
        Clipboard.SetContent(package);
        button.Content = "Copied";
        AutomationProperties.SetName(button, $"Copied {language} code");
    }

    private static TextBlock CreatePlainText(string text) => new()
    {
        Style = Resource<Style>("PiBodyTextStyle"),
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private static string NormalizeLanguage(string? info)
    {
        var language = (info ?? string.Empty).Trim();
        var separator = language.IndexOfAny([' ', '\t', '{']);
        if (separator >= 0)
        {
            language = language[..separator];
        }

        return language.ToLowerInvariant() switch
        {
            "c#" or "cs" or "dotnet" => "csharp",
            "js" or "node" => "javascript",
            "ts" => "typescript",
            "ps1" or "pwsh" => "powershell",
            "py" => "python",
            "sh" or "shell" => "bash",
            "yml" => "yaml",
            "html" => "html",
            "xml" => "xml",
            "json" => "json",
            "sql" => "sql",
            var value => value,
        };
    }

    private static string DisplayLanguage(string language) => language switch
    {
        "" => "Code",
        "csharp" => "C#",
        "javascript" => "JavaScript",
        "typescript" => "TypeScript",
        "powershell" => "PowerShell",
        "python" => "Python",
        "bash" => "Bash",
        "yaml" => "YAML",
        "html" => "HTML",
        "xml" => "XML",
        "json" => "JSON",
        "sql" => "SQL",
        _ => language,
    };

    private static bool TryCreateSafeUri(string? value, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
               candidate.Scheme is "http" or "https" or "mailto" &&
               (uri = candidate) is not null;
    }

    private static string InlineText(ContainerInline container, string source)
    {
        var builder = new System.Text.StringBuilder();
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case ContainerInline nested:
                    builder.Append(InlineText(nested, source));
                    break;
                default:
                    builder.Append(SourceText(inline, source));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string SourceText(MarkdownObject markdownObject, string source)
    {
        var span = markdownObject.Span;
        if (span.Start < 0 || span.End < span.Start || span.Start >= source.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(span.End, source.Length - 1) - span.Start + 1;
        return source.Substring(span.Start, length);
    }

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
