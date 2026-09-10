using ColorCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PiStation.ClientRuntime;
using PiStation.App.Views;
using PiStation.App.ViewModels;
using System.ComponentModel;

namespace PiStation.App.Views.Controls;

public sealed class DiffView : UserControl
{
    private readonly ListView _lines = new() { SelectionMode = ListViewSelectionMode.Extended, IsItemClickEnabled = true };
    private readonly ComboBox _layout = new() { ItemsSource = new[] { "Unified", "Split" }, SelectedIndex = 0, MinWidth = 90 };
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private IReadOnlyList<DiffLine> _document = [];
    private ShellLayoutViewModel? _preferences;
    private bool _synchronizingLayout;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(DiffView),
        new PropertyMetadata(string.Empty, static (owner, _) => ((DiffView)owner).LoadDocument()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public int SelectionStart => SelectedLines().Select(static line => line.Offset).DefaultIfEmpty(0).Min();
    public int SelectionLength => SelectedLines().Select(static line => line.Offset + line.Length).DefaultIfEmpty(SelectionStart).Max() - SelectionStart;
    public string? SelectedPath => SelectedLines().Select(static line => line.Path).Distinct(StringComparer.Ordinal).Count() == 1 ? SelectedLines().FirstOrDefault()?.Path : null;
    public int? SelectedStartLine => SelectedLines().Select(static line => line.NewLine ?? line.OldLine).Where(static line => line is not null).Min();
    public int? SelectedEndLine => SelectedLines().Select(static line => line.NewLine ?? line.OldLine).Where(static line => line is not null).Max();

    public DiffView()
    {
        var grid = new Grid { RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        toolbar.Children.Add(_layout);
        AutomationProperties.SetName(_layout, "Diff layout");
        AddButton(toolbar, "Previous hunk", () => NavigateHunk(-1));
        AddButton(toolbar, "Next hunk", () => NavigateHunk(1));
        AddButton(toolbar, "Collapse / expand", () =>
        {
            if (_collapsed.Count > 0) _collapsed.Clear();
            else foreach (var path in _document.Select(static line => line.Path).Distinct(StringComparer.Ordinal)) _collapsed.Add(path);
            Render();
        });
        grid.Children.Add(new ScrollViewer { Content = toolbar, HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled });
        _lines.ItemContainerStyle = new Style(typeof(ListViewItem));
        _lines.ItemContainerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _lines.ItemContainerStyle.Setters.Add(new Setter(MinHeightProperty, 22d));
        _lines.ItemContainerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        _lines.ItemTemplate = (DataTemplate)Application.Current.Resources["PiDiffLineTemplate"];
        _lines.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is DiffRow { Left.Kind: DiffLineKind.File } row)
            {
                if (!_collapsed.Add(row.Left.Path)) _collapsed.Remove(row.Left.Path);
                Render();
            }
        };
        _layout.SelectionChanged += (_, _) =>
        {
            if (_synchronizingLayout) return;
            if (_preferences is not null) _preferences.DiffLayoutIndex = _layout.SelectedIndex;
            Render();
        };
        AutomationProperties.SetAutomationId(_layout, "DiffLayoutSelector");
        Loaded += (_, _) =>
        {
            DetachPreferences();
            _preferences = AppearanceResources.Settings(this);
            if (_preferences is not null) _preferences.PropertyChanged += OnAppearanceChanged;
            SynchronizePreferences();
        };
        Unloaded += (_, _) => DetachPreferences();
        SizeChanged += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
        Grid.SetRow(_lines, 1);
        grid.Children.Add(_lines);
        Content = grid;
        AutomationProperties.SetName(_lines, "Changed lines. Select a range to add review context.");
        AutomationProperties.SetAutomationId(_lines, "DiffLines");
    }

    private void DetachPreferences()
    {
        if (_preferences is not null) _preferences.PropertyChanged -= OnAppearanceChanged;
        _preferences = null;
    }
    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellLayoutViewModel.Appearance)) SynchronizePreferences();
    }
    private void SynchronizePreferences()
    {
        _synchronizingLayout = true;
        try { _layout.SelectedIndex = _preferences?.DiffLayoutIndex ?? 0; }
        finally { _synchronizingLayout = false; }
        var wrap = _preferences?.WordWrap ?? true;
        ScrollViewer.SetHorizontalScrollMode(_lines, wrap ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_lines, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        AutomationProperties.SetHelpText(this, $"{(_layout.SelectedIndex == 1 ? "Split" : "Unified")} diff • word wrap {(wrap ? "on" : "off")}");
        Render();
    }

    private static void AddButton(Panel parent, string label, Action action)
    {
        var button = new Button { Content = label, Style = (Style)Application.Current.Resources["PiToolbarButtonStyle"] };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }

    private IEnumerable<DiffLine> SelectedLines() => _lines.SelectedItems.OfType<DiffRow>()
        .SelectMany(static row => row.Right is { } right ? new[] { row.Left, right } : [row.Left]);

    private void LoadDocument()
    {
        // Offsets belong to one patch. A whitespace reload or another file must not reuse
        // selection coordinates from the previous document; layout-only renders retain them.
        _lines.SelectedItems.Clear();
        _document = DiffDocument.Parse(Text ?? string.Empty); _collapsed.Clear(); Render();
    }

    private void NavigateHunk(int delta)
    {
        var rows = _lines.Items.OfType<DiffRow>().ToArray();
        if (rows.Length == 0) return;
        var start = _lines.SelectedIndex;
        for (var offset = 1; offset <= rows.Length; offset++)
        {
            var index = (Math.Max(start, 0) + delta * offset + rows.Length * 2) % rows.Length;
            if (rows[index].Left.Kind != DiffLineKind.Hunk) continue;
            _lines.SelectedItem = rows[index];
            _lines.ScrollIntoView(rows[index], ScrollIntoViewAlignment.Leading);
            return;
        }
    }

    private void Render()
    {
        var selectedOffsets = SelectedLines().Select(line => line.Offset).ToHashSet();
        var split = _layout.SelectedIndex == 1;
        var rows = new List<DiffRow>();
        var visible = _document.Where(line => line.Kind == DiffLineKind.File || !_collapsed.Contains(line.Path)).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            var line = visible[index];
            if (split && line.Kind == DiffLineKind.Deletion)
            {
                var removed = new List<DiffLine>();
                while (index < visible.Length && visible[index].Kind == DiffLineKind.Deletion) removed.Add(visible[index++]);
                var added = new List<DiffLine>();
                while (index < visible.Length && visible[index].Kind == DiffLineKind.Addition) added.Add(visible[index++]);
                for (var pair = 0; pair < Math.Max(removed.Count, added.Count); pair++)
                    rows.Add(new DiffRow(pair < removed.Count ? removed[pair] : added[pair], pair < added.Count && pair < removed.Count ? added[pair] : null, true, ActualWidth / 2, ActualTheme, this));
                index--;
            }
            else rows.Add(new DiffRow(line, split && line.Kind == DiffLineKind.Context ? line : null, split, ActualWidth / 2, ActualTheme, this));
        }
        _lines.ItemsSource = rows;
        foreach (var row in rows.Where(row => selectedOffsets.Contains(row.Left.Offset) || (row.Right is not null && selectedOffsets.Contains(row.Right.Offset)))) _lines.SelectedItems.Add(row);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new DiffViewPeer(this);

    private sealed class DiffViewPeer(DiffView owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => true;
        public string Value => owner.Text;
        public void SetValue(string value) => throw new InvalidOperationException("Diffs are read-only.");
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
        protected override string GetClassNameCore() => nameof(DiffView);
    }

    public sealed record DiffRow(DiffLine Left, DiffLine? Right, bool Split, double Width, ElementTheme Theme = ElementTheme.Default, FrameworkElement? Owner = null)
    {
        public string LeftText => Split && Left.Kind == DiffLineKind.Addition ? string.Empty : Left.Text;
        public string RightText => Right?.Text ?? (Split && Left.Kind == DiffLineKind.Addition ? Left.Text : string.Empty);
        public string LeftNumbers => Split ? $"{Left.OldLine}" : $"{Left.OldLine,4} {Left.NewLine,4}";
        public string RightNumbers => $"{Right?.NewLine ?? (Left.Kind == DiffLineKind.Addition ? Left.NewLine : null)}";
        public Visibility RightVisibility => Split ? Visibility.Visible : Visibility.Collapsed;
        public double RightWidth => Math.Max(120, Width - 12);
        public Brush LeftBackground => Background(Split && Left.Kind == DiffLineKind.Addition ? DiffLineKind.Context : Left.Kind);
        public Brush RightBackground => Background(Right?.Kind ?? (Left.Kind == DiffLineKind.Addition ? DiffLineKind.Addition : DiffLineKind.Context));
        public string SyntaxLanguage => Path.GetExtension(Left.Path).ToLowerInvariant() switch
        {
            ".cs" => "csharp", ".js" or ".jsx" or ".ts" or ".tsx" => "javascript", ".py" => "python",
            ".json" => "json", ".html" => "html", ".css" => "css", ".xml" or ".xaml" => "xml", _ => string.Empty,
        };
        private Brush Background(DiffLineKind kind)
        {
            var key = kind switch { DiffLineKind.Addition => "PiSuccessSurfaceBrush", DiffLineKind.Deletion => "PiCriticalSurfaceBrush", _ => Owner is null ? "PiControlSurfaceBrush" : "PiCodeBackgroundBrush" };
            return Owner is null ? ThemeResourceLookup.Get<Brush>(Theme, key) : ThemeResourceLookup.Get<Brush>(Owner, key);
        }
    }
}

public sealed class DiffCodeLine : UserControl
{
    public DiffCodeLine()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        Loaded += (_, _) => Render();
        ActualThemeChanged += (_, _) => Render();
    }

    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(string), typeof(DiffCodeLine), new PropertyMetadata(string.Empty, Changed));
    public static readonly DependencyProperty SyntaxLanguageProperty = DependencyProperty.Register(nameof(SyntaxLanguage), typeof(string), typeof(DiffCodeLine), new PropertyMetadata(string.Empty, Changed));
    public string Code { get => (string)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public string SyntaxLanguage { get => (string)GetValue(SyntaxLanguageProperty); set => SetValue(SyntaxLanguageProperty, value); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((DiffCodeLine)sender).Render();
    private void Render()
    {
        var block = new RichTextBlock
        {
            FontFamily = ThemeResourceLookup.Get<FontFamily>(this, "PiMonospaceFontFamily"),
            FontSize = ThemeResourceLookup.Get<double>(this, "PiCodeFontSize"), Foreground = Foreground,
            TextWrapping = AppearanceResources.Settings(this)?.CodeTextWrapping ?? TextWrapping.Wrap, IsHitTestVisible = false,
        };
        var paragraph = new Paragraph();
        var language = string.IsNullOrWhiteSpace(SyntaxLanguage) ? null : Languages.FindById(SyntaxLanguage);
        if (language is null || (Code?.Length ?? 0) > 4096) paragraph.Inlines.Add(new Run { Text = Code ?? string.Empty });
        else SyntaxHighlighting.CreateFormatter(ActualTheme).FormatInlines(Code ?? string.Empty, language, paragraph.Inlines);
        block.Blocks.Add(paragraph);
        Content = block;
    }
}
