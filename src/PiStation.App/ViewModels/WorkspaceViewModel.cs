using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class WorkspaceViewModel : ObservableObject
{
    private bool _isShowingArchivedThreads;
    private ProjectDescriptor? _selectedProject;
    private ThreadDescriptor? _selectedThread;
    private string _threadLifecycleStatus = string.Empty;
    private string _threadListStatus = "Select a workspace to see its threads";
    private string _threadSearchQuery = string.Empty;

    public ObservableCollection<ProjectDescriptor> Projects { get; } = [];

    public ObservableCollection<ThreadDescriptor> Threads { get; } = [];

    public ProjectDescriptor? SelectedProject
    {
        get => _selectedProject;
        internal set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                OnPropertyChanged(nameof(HasSelectedProject));
                OnPropertyChanged(nameof(ActiveProjectName));
                OnPropertyChanged(nameof(ActiveProjectPath));
                OnPropertyChanged(nameof(NoProjectEmptyStateVisibility));
                OnPropertyChanged(nameof(NoThreadEmptyStateVisibility));
            }
        }
    }

    public ThreadDescriptor? SelectedThread
    {
        get => _selectedThread;
        internal set
        {
            if (SetProperty(ref _selectedThread, value))
            {
                OnPropertyChanged(nameof(HasSelectedThread));
                OnPropertyChanged(nameof(ActiveThreadTitle));
                OnPropertyChanged(nameof(LinkedPullRequestSummary));
                OnPropertyChanged(nameof(LinkedPullRequestVisibility));
                OnPropertyChanged(nameof(PiConfigurationVisibility));
                OnPropertyChanged(nameof(NoThreadEmptyStateVisibility));
            }
        }
    }

    public bool HasSelectedProject => SelectedProject is not null;

    public bool HasSelectedThread => SelectedThread is not null;

    public string ActiveProjectName => SelectedProject?.DisplayName ?? "Local workspace";

    public string ActiveProjectPath => SelectedProject?.CanonicalPath ?? "No project selected";

    public string ActiveThreadTitle => SelectedThread?.Title ?? "No active thread";

    public string LinkedPullRequestSummary => SelectedThread?.PullRequest is { } link
        ? $"{link.Provider} · {link.Repository} #{link.Number}\n{link.Title}\n{link.State}" : string.Empty;

    public Visibility LinkedPullRequestVisibility => SelectedThread?.PullRequest is not null
        ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoProjectEmptyStateVisibility => HasSelectedProject
        ? Visibility.Collapsed
        : Visibility.Visible;

    public Visibility NoThreadEmptyStateVisibility => HasSelectedProject && !HasSelectedThread
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility PiConfigurationVisibility => HasSelectedThread
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ThreadSearchQuery
    {
        get => _threadSearchQuery;
        internal set
        {
            if (SetProperty(ref _threadSearchQuery, value))
            {
                OnPropertyChanged(nameof(ThreadSearchClearVisibility));
            }
        }
    }

    public Visibility ThreadSearchClearVisibility => string.IsNullOrEmpty(ThreadSearchQuery)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public bool IsShowingArchivedThreads
    {
        get => _isShowingArchivedThreads;
        internal set
        {
            if (SetProperty(ref _isShowingArchivedThreads, value))
            {
                OnPropertyChanged(nameof(ThreadArchiveViewLabel));
            }
        }
    }

    public string ThreadArchiveViewLabel => IsShowingArchivedThreads
        ? "Showing archived threads"
        : "Show archived threads";

    public string ThreadListStatus
    {
        get => _threadListStatus;
        internal set
        {
            if (SetProperty(ref _threadListStatus, value))
            {
                OnPropertyChanged(nameof(ThreadListStatusVisibility));
            }
        }
    }

    public Visibility ThreadListStatusVisibility => string.IsNullOrEmpty(ThreadListStatus)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string ThreadLifecycleStatus
    {
        get => _threadLifecycleStatus;
        internal set
        {
            if (SetProperty(ref _threadLifecycleStatus, value))
            {
                OnPropertyChanged(nameof(ThreadLifecycleStatusVisibility));
            }
        }
    }

    public Visibility ThreadLifecycleStatusVisibility => string.IsNullOrEmpty(ThreadLifecycleStatus)
        ? Visibility.Collapsed
        : Visibility.Visible;
}
