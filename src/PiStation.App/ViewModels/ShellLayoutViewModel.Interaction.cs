namespace PiStation.App.ViewModels;

public sealed partial class ShellLayoutViewModel
{
    private int _quitConfirmationModeIndex = 1;
    private bool _proactivePanelsEnabled;
    private bool _composerCollapseOnBlur = true;
    private bool _composerCollapseOnScroll = true;
    private bool _showSkillsInSlashMenu = true;

    public int QuitConfirmationModeIndex
    {
        get => _quitConfirmationModeIndex;
        set { if (value is >= 0 and <= 2 && SetProperty(ref _quitConfirmationModeIndex, value)) Save(); }
    }

    public bool ProactivePanelsEnabled
    {
        get => _proactivePanelsEnabled;
        set { if (SetProperty(ref _proactivePanelsEnabled, value)) Save(); }
    }

    public bool ComposerCollapseOnBlur
    {
        get => _composerCollapseOnBlur;
        set { if (SetProperty(ref _composerCollapseOnBlur, value)) Save(); }
    }

    public bool ComposerCollapseOnScroll
    {
        get => _composerCollapseOnScroll;
        set { if (SetProperty(ref _composerCollapseOnScroll, value)) Save(); }
    }

    public bool ShowSkillsInSlashMenu
    {
        get => _showSkillsInSlashMenu;
        set { if (SetProperty(ref _showSkillsInSlashMenu, value)) Save(); }
    }
}
