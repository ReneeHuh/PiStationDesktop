using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class PiToolSelectionViewModel : ObservableObject
{
    private int _modeIndex;
    private string _allowed = string.Empty, _excluded = string.Empty;
    public int ModeIndex
    {
        get => _modeIndex;
        set { if (value is >= 0 and <= 2 && SetProperty(ref _modeIndex, value)) OnPropertyChanged(nameof(IsAllowlist)); }
    }
    public bool IsAllowlist => ModeIndex == (int)PiToolSelectionMode.Allowlist;
    public string Allowed { get => _allowed; set => SetProperty(ref _allowed, value); }
    public string Excluded { get => _excluded; set => SetProperty(ref _excluded, value); }
    private static string[] Names(string text) => text.Split([',', '\r', '\n'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public PiToolSelection Create() => PiToolSelectionRules.Normalize(new((PiToolSelectionMode)ModeIndex, Names(Allowed), Names(Excluded)));
    public void Apply(PiToolSelection? selection)
    {
        var value = PiToolSelectionRules.Normalize(selection ?? new());
        ModeIndex = (int)value.Mode;
        Allowed = string.Join(", ", value.Allowed!);
        Excluded = string.Join(", ", value.Excluded!);
    }
    public void UseReadOnlyPreset() { ModeIndex = 1; Allowed = "read, grep, find, ls"; }
    public void UseWindowsPreset() { ModeIndex = 1; Allowed = "read, grep, find, ls, powershell, edit, write"; }
    public void AddPowerShell()
    {
        if (!IsAllowlist) return;
        Allowed = string.Join(", ", Names(Allowed).Append("powershell").Distinct(StringComparer.Ordinal));
    }
}

public sealed class PiToolRow(PiToolDescriptor tool)
{
    public string Name => tool.Name;
    public string Description => tool.Description;
    public string State => (tool.Active ? "Active" : "Registered, inactive") + " · " + tool.Source;
}

public sealed class PiToolInventoryViewModel : ObservableObject
{
    private string _summary = "Refresh an idle thread to inspect its effective tool inventory.";
    private string _powerShell = "PowerShell availability has not been inspected.";
    public ObservableCollection<PiToolRow> Tools { get; } = [];
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string PowerShell { get => _powerShell; private set => SetProperty(ref _powerShell, value); }
    public void Apply(PiToolInventory? inventory)
    {
        Tools.Clear();
        if (inventory is null)
        {
            Summary = "This runtime did not report a tool inventory. Restart with the current PiStation management extension and refresh.";
            PowerShell = "PowerShell availability is unknown; no inventory was reported.";
            return;
        }
        foreach (var tool in inventory.Tools.OrderBy(item => item.Name, StringComparer.Ordinal)) Tools.Add(new(tool));
        var policy = inventory.Selection ?? new();
        var mode = policy.Mode switch { PiToolSelectionMode.Allowlist => "explicit allowlist", PiToolSelectionMode.None => "no tools", _ => "Pi defaults / legacy launch flags" };
        var missing = (policy.Mode == PiToolSelectionMode.Allowlist ? policy.Allowed ?? [] : [])
            .Except(policy.Excluded ?? [], StringComparer.Ordinal).Except(inventory.Tools.Select(tool => tool.Name), StringComparer.Ordinal).ToArray();
        Summary = $"At last refresh: {inventory.Tools.Count(tool => tool.Active)} active / {inventory.Tools.Count} registered. Runtime launch policy: {mode}. " +
            $"Excluded: {(policy.Excluded?.Count > 0 ? string.Join(", ", policy.Excluded) : "none")}. Active means selected for the model; approval/planning restrictions still apply. Refresh after restart, plan, or extension changes." +
            (inventory.Truncated ? " Inventory truncated; absence is not conclusive." : missing.Length > 0 ? " Requested but not registered: " + string.Join(", ", missing) + "." : "");
        var shell = inventory.Tools.FirstOrDefault(tool => tool.Name == "powershell");
        PowerShell = shell is null ? "PowerShell is not in the reported registry (excluded, unavailable, or omitted by the runtime)."
            : shell.Active ? "PowerShell is active. Execution uses the host's PowerShell and remains subject to approval/planning policies."
            : "PowerShell is registered but inactive in this runtime.";
    }
    public void Clear()
    {
        Tools.Clear();
        Summary = "Refresh an idle thread to inspect its effective tool inventory.";
        PowerShell = "PowerShell availability has not been inspected.";
    }
}
