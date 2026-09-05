using System.Globalization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class DraftAttachmentViewModel(DraftAttachment attachment)
{
    public DraftAttachment Attachment { get; } = attachment ?? throw new ArgumentNullException(nameof(attachment));

    public AttachmentId AttachmentId => Attachment.AttachmentId;

    public string FileName => Attachment.FileName;

    public string Detail => $"{FormatBytes(Attachment.ByteLength)} • {Attachment.MediaType}";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var units = new[] { "KB", "MB", "GB" };
        var value = bytes / 1024d;
        var unit = units[0];
        for (var index = 1; index < units.Length && value >= 1024; index++)
        {
            value /= 1024;
            unit = units[index];
        }

        return $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {unit}";
    }
}
