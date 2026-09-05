using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class FileMentionViewModel : ObservableObject
{
    private bool _isOpen;
    private int _selectedIndex = -1;
    private string _status = string.Empty;

    public ObservableCollection<ProjectFileMatch> Suggestions { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        internal set
        {
            if (SetProperty(ref _isOpen, value))
            {
                OnPropertyChanged(nameof(SuggestionsVisibility));
            }
        }
    }

    public Visibility SuggestionsVisibility => IsOpen ? Visibility.Visible : Visibility.Collapsed;

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetProperty(ref _selectedIndex, value);
    }
}
