using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record PiSessionImportRequest(Guid OperationId, ProjectId ProjectId, string Extension,
    long ByteLength, string Sha256, string? Title = null)
{
    public void Validate()
    {
        if (OperationId == Guid.Empty || string.IsNullOrWhiteSpace(ProjectId.Value) || ProjectId.Value.Length > 256 ||
            Extension is not (".jsonl" or ".zip") || ByteLength <= 0 ||
            ByteLength > (Extension == ".jsonl" ? PiSessionTransferDefaults.MaximumJsonlBytes : PiSessionTransferDefaults.MaximumTransferBytes) ||
            Sha256 is not { Length: 64 } || !Sha256.All(Uri.IsHexDigit) || Title?.Length > 512)
            throw new ArgumentException("Choose a valid Pi JSONL session (up to 128 MiB) or ZIP bundle (up to 1 GiB).");
    }
}

public static class PiSessionTransferDefaults
{
    public const long MaximumJsonlBytes = 128L * 1024 * 1024;
    public const long MaximumTransferBytes = 1024L * 1024 * 1024;

    public static string Extension(PiSessionExportFormat format) => format switch
    {
        PiSessionExportFormat.Jsonl => ".jsonl",
        PiSessionExportFormat.Html => ".html",
        PiSessionExportFormat.Bundle => ".zip",
        _ => throw new ArgumentException("Select JSONL, HTML, or a ZIP bundle.", nameof(format)),
    };
}
