using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public enum WorkspaceFileSearchMode
{
    Paths,
    Contents,
}

public enum WorkbenchFilePreviewKind
{
    Text,
    Markdown,
    Html,
    Image,
    Pdf,
    Audio,
    Video,
    Binary,
}

public sealed class WorkbenchFilesViewModel : ObservableObject
{
    private readonly Dictionary<string, WorkspaceFileSession> _sessions = new(StringComparer.Ordinal);
    private WorkspaceFileSession _session = new();
    private int _searchModeIndex;
    private string _status = "Select a workspace to browse files";

    public ObservableCollection<WorkspaceTreeItemViewModel> TreeRoots => _session.TreeRoots;

    public ObservableCollection<ProjectFileMatch> Files => _session.Files;

    public ObservableCollection<ProjectContentMatch> ContentMatches => _session.ContentMatches;

    public ObservableCollection<WorkbenchFileDocumentViewModel> OpenDocuments => _session.OpenDocuments;

    public string SearchQuery
    {
        get => _session.SearchQuery;
        internal set
        {
            if (_session.SearchQuery == value)
            {
                return;
            }

            _session.SearchQuery = value;
            OnPropertyChanged();
            NotifySearchPresentationChanged();
        }
    }

    public int SearchModeIndex
    {
        get => _searchModeIndex;
        internal set
        {
            if (SetProperty(ref _searchModeIndex, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(SearchMode));
                NotifySearchPresentationChanged();
            }
        }
    }

    public WorkspaceFileSearchMode SearchMode => SearchModeIndex == 1
        ? WorkspaceFileSearchMode.Contents
        : WorkspaceFileSearchMode.Paths;

    public string SearchPlaceholder => SearchMode == WorkspaceFileSearchMode.Contents
        ? "Search file contents"
        : "Filter files by name";

    public bool CaseSensitive
    {
        get => _session.CaseSensitive;
        internal set
        {
            if (_session.CaseSensitive != value)
            {
                _session.CaseSensitive = value;
                OnPropertyChanged();
            }
        }
    }

    public bool WholeWord
    {
        get => _session.WholeWord;
        internal set
        {
            if (_session.WholeWord != value)
            {
                _session.WholeWord = value;
                OnPropertyChanged();
            }
        }
    }

    public bool UseRegularExpression
    {
        get => _session.UseRegularExpression;
        internal set
        {
            if (_session.UseRegularExpression != value)
            {
                _session.UseRegularExpression = value;
                OnPropertyChanged();
            }
        }
    }

    public Visibility ContentSearchOptionsVisibility => SearchMode == WorkspaceFileSearchMode.Contents
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string Status
    {
        get => _status;
        internal set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }
    }

    public Visibility StatusVisibility => string.IsNullOrEmpty(Status)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public WorkbenchFileDocumentViewModel? ActiveDocument
    {
        get => _session.ActiveDocument;
        internal set
        {
            if (ReferenceEquals(_session.ActiveDocument, value))
            {
                return;
            }

            _session.ActiveDocument = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DocumentVisibility));
            OnPropertyChanged(nameof(EmptyDocumentVisibility));
        }
    }

    public ProjectFileMatch? SelectedFile
    {
        get => _session.SelectedFile;
        internal set
        {
            if (Equals(_session.SelectedFile, value))
            {
                return;
            }

            _session.SelectedFile = value;
            OnPropertyChanged();
        }
    }

    public ProjectContentMatch? SelectedContentMatch
    {
        get => _session.SelectedContentMatch;
        internal set
        {
            if (Equals(_session.SelectedContentMatch, value))
            {
                return;
            }

            _session.SelectedContentMatch = value;
            OnPropertyChanged();
        }
    }

    public Visibility TreeVisibility => SearchMode == WorkspaceFileSearchMode.Paths &&
                                        string.IsNullOrEmpty(SearchQuery)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PathResultsVisibility => SearchMode == WorkspaceFileSearchMode.Paths &&
                                               !string.IsNullOrEmpty(SearchQuery)
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ContentResultsVisibility => SearchMode == WorkspaceFileSearchMode.Contents
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility DocumentVisibility => ActiveDocument is null
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility EmptyDocumentVisibility => ActiveDocument is null
        ? Visibility.Visible
        : Visibility.Collapsed;

    internal void SwitchContext(string? contextKey, bool hasProject)
    {
        _session = contextKey is null
            ? new WorkspaceFileSession()
            : _sessions.GetValueOrDefault(contextKey) ?? AddSession(contextKey);
        _searchModeIndex = (int)_session.SearchMode;
        Status = hasProject ? "Loading workspace tree…" : "Select a workspace to browse files";
        RaiseSessionProperties();
    }

    internal void ApplyEntries(IReadOnlyList<ProjectWorkspaceEntry> entries)
    {
        TreeRoots.Clear();
        var byPath = new Dictionary<string, WorkspaceTreeItemViewModel>(StringComparer.OrdinalIgnoreCase);

        WorkspaceTreeItemViewModel EnsureDirectory(string path)
        {
            if (byPath.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var separator = path.LastIndexOf('/');
            var name = separator < 0 ? path : path[(separator + 1)..];
            var item = new WorkspaceTreeItemViewModel(path, name, isDirectory: true, 0);
            byPath[path] = item;
            if (separator < 0)
            {
                TreeRoots.Add(item);
            }
            else
            {
                EnsureDirectory(path[..separator]).Children.Add(item);
            }

            return item;
        }

        foreach (var entry in entries.Where(static entry => entry.IsDirectory))
        {
            EnsureDirectory(entry.RelativePath);
        }

        foreach (var entry in entries.Where(static entry => !entry.IsDirectory))
        {
            var item = new WorkspaceTreeItemViewModel(
                entry.RelativePath,
                entry.Name,
                isDirectory: false,
                entry.ByteLength);
            byPath[entry.RelativePath] = item;
            var separator = entry.RelativePath.LastIndexOf('/');
            if (separator < 0)
            {
                TreeRoots.Add(item);
            }
            else
            {
                EnsureDirectory(entry.RelativePath[..separator]).Children.Add(item);
            }
        }

        SortTree(TreeRoots);
    }

    internal WorkbenchFileDocumentViewModel OpenDocument(string relativePath, int? revealLine = null)
    {
        var document = OpenDocuments.FirstOrDefault(candidate =>
            string.Equals(candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            document = new WorkbenchFileDocumentViewModel(relativePath);
            OpenDocuments.Add(document);
        }

        document.RequestLineReveal(revealLine);
        ActiveDocument = document;
        return document;
    }

    internal WorkbenchFileDocumentViewModel OpenExternalDocument(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var normalized = Path.GetFullPath(absolutePath);
        var document = OpenDocuments.FirstOrDefault(candidate =>
            candidate.IsExternal &&
            string.Equals(candidate.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            document = new WorkbenchFileDocumentViewModel(normalized, isExternal: true);
            OpenDocuments.Add(document);
        }

        ActiveDocument = document;
        return document;
    }

    internal void CloseDocument(WorkbenchFileDocumentViewModel document)
    {
        var index = OpenDocuments.IndexOf(document);
        if (index < 0)
        {
            return;
        }

        OpenDocuments.RemoveAt(index);
        if (ReferenceEquals(ActiveDocument, document))
        {
            ActiveDocument = OpenDocuments.Count == 0
                ? null
                : OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)];
        }
    }

    internal void SetSearchMode(int index)
    {
        SearchModeIndex = index;
        _session.SearchMode = SearchMode;
        SelectedFile = null;
        SelectedContentMatch = null;
    }

    internal void NotifySearchPresentationChanged()
    {
        OnPropertyChanged(nameof(TreeVisibility));
        OnPropertyChanged(nameof(PathResultsVisibility));
        OnPropertyChanged(nameof(ContentResultsVisibility));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(ContentSearchOptionsVisibility));
    }

    private WorkspaceFileSession AddSession(string contextKey)
    {
        var session = new WorkspaceFileSession();
        _sessions.Add(contextKey, session);
        return session;
    }

    private void RaiseSessionProperties()
    {
        OnPropertyChanged(nameof(TreeRoots));
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(ContentMatches));
        OnPropertyChanged(nameof(OpenDocuments));
        OnPropertyChanged(nameof(SearchQuery));
        OnPropertyChanged(nameof(SearchModeIndex));
        OnPropertyChanged(nameof(SearchMode));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(CaseSensitive));
        OnPropertyChanged(nameof(WholeWord));
        OnPropertyChanged(nameof(UseRegularExpression));
        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(SelectedContentMatch));
        OnPropertyChanged(nameof(ActiveDocument));
        NotifySearchPresentationChanged();
        OnPropertyChanged(nameof(DocumentVisibility));
        OnPropertyChanged(nameof(EmptyDocumentVisibility));
    }

    private static void SortTree(ObservableCollection<WorkspaceTreeItemViewModel> items)
    {
        var ordered = items
            .OrderByDescending(static item => item.IsDirectory)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();
        items.Clear();
        foreach (var item in ordered)
        {
            SortTree(item.Children);
            items.Add(item);
        }
    }

    private sealed class WorkspaceFileSession
    {
        public ObservableCollection<WorkspaceTreeItemViewModel> TreeRoots { get; } = [];
        public ObservableCollection<ProjectFileMatch> Files { get; } = [];
        public ObservableCollection<ProjectContentMatch> ContentMatches { get; } = [];
        public ObservableCollection<WorkbenchFileDocumentViewModel> OpenDocuments { get; } = [];
        public string SearchQuery { get; set; } = string.Empty;
        public WorkspaceFileSearchMode SearchMode { get; set; }
        public bool CaseSensitive { get; set; }
        public bool WholeWord { get; set; }
        public bool UseRegularExpression { get; set; }
        public ProjectFileMatch? SelectedFile { get; set; }
        public ProjectContentMatch? SelectedContentMatch { get; set; }
        public WorkbenchFileDocumentViewModel? ActiveDocument { get; set; }
    }
}

public sealed class WorkspaceTreeItemViewModel(
    string relativePath,
    string name,
    bool isDirectory,
    long byteLength) : ObservableObject
{
    public string RelativePath { get; } = relativePath;
    public string Name { get; } = name;
    public bool IsDirectory { get; } = isDirectory;
    public long ByteLength { get; } = byteLength;
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public ObservableCollection<WorkspaceTreeItemViewModel> Children { get; } = [];
}

public sealed class WorkbenchFileDocumentViewModel : ObservableObject
{
    private byte[]? _assetContent;
    private string _content = string.Empty;
    private bool _isDirty;
    private bool _isBinary;
    private bool _isLoading = true;
    private bool _isSaving;
    private bool _isTruncated;
    private int _revealRequestId;
    private int? _revealLine;
    private string _revision = string.Empty;
    private bool _showRenderedContent;
    private string? _localPreviewPath;
    private string _status = "Loading…";

    public WorkbenchFileDocumentViewModel(string relativePath, bool isExternal = false)
    {
        RelativePath = relativePath;
        FileName = Path.GetFileName(relativePath);
        IsExternal = isExternal;
        var extension = Path.GetExtension(relativePath);
        IsMarkdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase);
        IsImage = extension.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        IsHtml = extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
        IsPdf = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        IsAudio = extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".oga", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".wma", StringComparison.OrdinalIgnoreCase);
        IsVideo = extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
        PreviewKind = IsMarkdown
            ? WorkbenchFilePreviewKind.Markdown
            : IsHtml
                ? WorkbenchFilePreviewKind.Html
                : IsImage
                    ? WorkbenchFilePreviewKind.Image
                    : IsPdf
                        ? WorkbenchFilePreviewKind.Pdf
                        : IsAudio
                            ? WorkbenchFilePreviewKind.Audio
                            : IsVideo
                                ? WorkbenchFilePreviewKind.Video
                                : WorkbenchFilePreviewKind.Text;
        // Match T3's source-first default while keeping rendered Markdown one click away.
        _showRenderedContent = IsHtml;
    }

    public string RelativePath { get; }
    public string FileName { get; }
    public bool IsMarkdown { get; }
    public bool IsImage { get; }
    public bool IsHtml { get; }
    public bool IsPdf { get; }
    public bool IsAudio { get; }
    public bool IsVideo { get; }
    public bool IsMedia => IsAudio || IsVideo;
    public bool IsExternal { get; }
    public WorkbenchFilePreviewKind PreviewKind { get; private set; }
    public bool UsesAssetContent => IsImage || IsPdf || IsMedia;
    public bool IsBinary => _isBinary;

    public string Content
    {
        get => _content;
        set
        {
            if (SetProperty(ref _content, value))
            {
                IsDirty = !_isLoading && !IsExternal && !UsesAssetContent;
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public byte[]? AssetContent
    {
        get => _assetContent;
        internal set => SetProperty(ref _assetContent, value);
    }

    public string? LocalPreviewPath
    {
        get => _localPreviewPath;
        internal set => SetProperty(ref _localPreviewPath, value);
    }

    public string Revision
    {
        get => _revision;
        internal set => SetProperty(ref _revision, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        internal set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsTruncated
    {
        get => _isTruncated;
        private set
        {
            if (SetProperty(ref _isTruncated, value))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(IsReadOnly));
            }
        }
    }

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public string DisplayTitle => IsDirty ? $"{FileName} ●" : FileName;
    public bool CanSave => IsDirty && !IsLoading && !IsSaving && !IsTruncated && !IsExternal && !UsesAssetContent && !IsBinary;
    public bool CanReloadFromWorkspace => !IsExternal;
    internal long ExternalReadVersion { get; private set; }
    internal long BeginExternalRead()
    {
        IsLoading = true;
        Status = "Loading artifact read-only…";
        return ++ExternalReadVersion;
    }
    public bool CanOpenInEditor => !IsExternal;
    public bool IsReadOnly => IsExternal || IsTruncated || UsesAssetContent || IsBinary;

    public bool ShowRenderedContent
    {
        get => _showRenderedContent;
        set
        {
            if (SetProperty(ref _showRenderedContent, (IsMarkdown || IsHtml) && value))
            {
                RaisePreviewVisibility();
            }
        }
    }

    public bool ShowRenderedMarkdown
    {
        get => ShowRenderedContent;
        set => ShowRenderedContent = value;
    }

    public Visibility SourceVisibility => !UsesAssetContent &&
                                          (!(IsMarkdown || IsHtml) || !ShowRenderedContent) &&
                                          PreviewKind != WorkbenchFilePreviewKind.Binary
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility MarkdownVisibility => IsMarkdown && ShowRenderedContent
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility HtmlVisibility => IsHtml && ShowRenderedContent
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PdfVisibility => IsPdf ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WebPreviewVisibility => (IsHtml && ShowRenderedContent) || IsPdf
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility MediaVisibility => IsMedia ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UnsupportedBinaryVisibility => PreviewKind == WorkbenchFilePreviewKind.Binary
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility RenderedToggleVisibility => IsMarkdown || IsHtml
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility MarkdownToggleVisibility => RenderedToggleVisibility;

    public int? RevealLine
    {
        get => _revealLine;
        private set => SetProperty(ref _revealLine, value);
    }

    public int RevealRequestId
    {
        get => _revealRequestId;
        private set => SetProperty(ref _revealRequestId, value);
    }

    internal void ApplyText(ReadProjectFileResult result)
    {
        _isLoading = true;
        _isBinary = result.IsBinary;
        if (result.IsBinary && !UsesAssetContent)
        {
            PreviewKind = WorkbenchFilePreviewKind.Binary;
        }
        Content = result.Content;
        Revision = result.Revision;
        IsTruncated = result.IsTruncated;
        IsDirty = false;
        IsLoading = false;
        Status = result.IsBinary
            ? $"Binary file • {result.ByteLength:N0} bytes"
            : result.IsTruncated
                ? $"{result.ByteLength:N0} bytes • first {FileReadDefaults.DefaultMaximumBytes:N0} bytes shown read-only"
                : $"{result.ByteLength:N0} bytes • editable";
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsBinary));
        OnPropertyChanged(nameof(PreviewKind));
        OnPropertyChanged(nameof(CanSave));
        RaisePreviewVisibility();
    }

    internal void ApplyAsset(ReadProjectFileAssetResult result)
    {
        _isBinary = true;
        Revision = result.Revision;
        AssetContent = result.Content;
        IsDirty = false;
        IsLoading = false;
        Status = $"{result.ByteLength:N0} bytes • {result.MediaType}";
        OnPropertyChanged(nameof(IsBinary));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanSave));
        RaisePreviewVisibility();
    }

    internal void ApplyExternalText(string content, long byteLength, bool isTruncated)
    {
        _isLoading = true;
        _isBinary = false;
        Content = content;
        Revision = string.Empty;
        IsTruncated = isTruncated;
        IsDirty = false;
        IsLoading = false;
        Status = isTruncated
            ? $"External read-only file • {byteLength:N0} bytes • preview truncated"
            : $"External read-only file • {byteLength:N0} bytes";
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanSave));
        RaisePreviewVisibility();
    }

    internal void ApplyExternalAsset(string localPreviewPath, long byteLength)
    {
        _isBinary = true;
        LocalPreviewPath = localPreviewPath;
        Revision = string.Empty;
        IsDirty = false;
        IsLoading = false;
        Status = $"External read-only file • {byteLength:N0} bytes";
        OnPropertyChanged(nameof(IsBinary));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanSave));
        RaisePreviewVisibility();
    }

    internal void ApplyExternalImage(byte[] content, long byteLength)
    {
        ArgumentNullException.ThrowIfNull(content);
        _isBinary = true;
        AssetContent = content;
        Revision = string.Empty;
        IsDirty = false;
        IsLoading = false;
        Status = $"External read-only image • {byteLength:N0} bytes";
        OnPropertyChanged(nameof(IsBinary));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(CanSave));
        RaisePreviewVisibility();
    }

    internal void ApplySaved(SaveProjectFileResult result)
    {
        Revision = result.Revision;
        IsDirty = false;
        IsSaving = false;
        Status = $"Saved • {result.ByteLength:N0} bytes";
    }

    internal void ApplyLoadFailure(string message)
    {
        IsLoading = false;
        Status = message;
    }

    internal void RequestLineReveal(int? line)
    {
        RevealLine = line;
        RevealRequestId++;
        if (line is not null && (IsMarkdown || IsHtml))
        {
            ShowRenderedContent = false;
        }
    }

    private void RaisePreviewVisibility()
    {
        OnPropertyChanged(nameof(SourceVisibility));
        OnPropertyChanged(nameof(MarkdownVisibility));
        OnPropertyChanged(nameof(HtmlVisibility));
        OnPropertyChanged(nameof(ImageVisibility));
        OnPropertyChanged(nameof(PdfVisibility));
        OnPropertyChanged(nameof(WebPreviewVisibility));
        OnPropertyChanged(nameof(MediaVisibility));
        OnPropertyChanged(nameof(UnsupportedBinaryVisibility));
    }
}
