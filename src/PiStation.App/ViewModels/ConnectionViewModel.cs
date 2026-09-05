using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Identifiers;

namespace PiStation.App.ViewModels;

public sealed class ConnectionViewModel : ObservableObject
{
    private bool _hasRuntimeError;
    private bool _hasTransportError;
    private bool _hasUncertainCommand;
    private string _runtimeErrorMessage = string.Empty;
    private string _status = "Local • Starting";
    private string _transportErrorMessage = string.Empty;
    private string _uncertainCommandMessage = string.Empty;

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public bool HasRuntimeError
    {
        get => _hasRuntimeError;
        internal set => SetProperty(ref _hasRuntimeError, value);
    }

    public string RuntimeErrorMessage
    {
        get => _runtimeErrorMessage;
        internal set => SetProperty(ref _runtimeErrorMessage, value);
    }

    public bool HasTransportError
    {
        get => _hasTransportError;
        internal set => SetProperty(ref _hasTransportError, value);
    }

    public string TransportErrorMessage
    {
        get => _transportErrorMessage;
        internal set => SetProperty(ref _transportErrorMessage, value);
    }

    public bool HasUncertainCommand
    {
        get => _hasUncertainCommand;
        internal set => SetProperty(ref _hasUncertainCommand, value);
    }

    public string UncertainCommandMessage
    {
        get => _uncertainCommandMessage;
        internal set => SetProperty(ref _uncertainCommandMessage, value);
    }

    internal CommandId? UncertainCommandId { get; set; }

    internal ThreadId? UncertainPiConfigurationThreadId { get; set; }

    internal void ShowRuntimeError(string message)
    {
        RuntimeErrorMessage = message;
        HasRuntimeError = true;
    }

    internal void ClearRuntimeError()
    {
        RuntimeErrorMessage = string.Empty;
        HasRuntimeError = false;
    }

    internal void ShowTransportError(string message)
    {
        TransportErrorMessage = message;
        HasTransportError = true;
    }

    internal void ClearTransportError()
    {
        TransportErrorMessage = string.Empty;
        HasTransportError = false;
    }

    internal void ShowUncertainCommand(
        CommandId commandId,
        string message,
        ThreadId? piConfigurationThreadId)
    {
        UncertainCommandId = commandId;
        UncertainPiConfigurationThreadId = piConfigurationThreadId;
        UncertainCommandMessage = message;
        HasUncertainCommand = true;
    }

    internal void ClearUncertainCommand()
    {
        UncertainCommandId = null;
        UncertainPiConfigurationThreadId = null;
        UncertainCommandMessage = string.Empty;
        HasUncertainCommand = false;
    }
}
