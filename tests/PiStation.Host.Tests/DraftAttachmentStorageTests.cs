using System.Security.Cryptography;
using PiStation.Host.Attachments;
using PiStation.Host.Errors;
using PiStation.Protocol;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class DraftAttachmentStorageTests
{
    [Fact]
    public async Task StoreCopiesToOwnedStorageAndComputesContentHash()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var storage = new DraftAttachmentStorage(options);
        var bytes = "host owned"u8.ToArray();
        var request = CreateRequest("notes.txt", "text/plain", bytes.Length);
        using var content = new MemoryStream(bytes);

        var stored = await storage.StoreAsync(request, content);

        Assert.True(stored.CreatedFile);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), stored.Attachment.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(stored.Attachment.ServerPath));
        Assert.StartsWith(
            Path.GetFullPath(options.AttachmentRoot) + Path.DirectorySeparatorChar,
            stored.Attachment.ServerPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreRejectsUnsafeNamesOversizeImagesAndLengthMismatches()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions() with
        {
            MaximumImageAttachmentBytes = 4,
            MaximumFileAttachmentBytes = 16,
        };
        var storage = new DraftAttachmentStorage(options);

        var unsafeName = await Assert.ThrowsAsync<HostOperationException>(() => storage.StoreAsync(
            CreateRequest("../secret.txt", "text/plain", 1),
            new MemoryStream([1])));
        var tooLarge = await Assert.ThrowsAsync<HostOperationException>(() => storage.StoreAsync(
            CreateRequest("image.png", "image/png", 5),
            new MemoryStream([1, 2, 3, 4, 5])));
        var mismatch = await Assert.ThrowsAsync<HostOperationException>(() => storage.StoreAsync(
            CreateRequest("notes.txt", "text/plain", 2),
            new MemoryStream([1])));

        Assert.Equal(ProtocolErrorCodes.AttachmentInvalid, unsafeName.Code);
        Assert.Equal(ProtocolErrorCodes.AttachmentTooLarge, tooLarge.Code);
        Assert.Equal(ProtocolErrorCodes.AttachmentInvalid, mismatch.Code);
        Assert.Empty(Directory.EnumerateFiles(options.AttachmentStagingRoot));
    }

    [Fact]
    public async Task PromptValidationRejectsContentChangedAfterUpload()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var storage = new DraftAttachmentStorage(temporaryDirectory.CreateOptions());
        var bytes = "trusted bytes"u8.ToArray();
        using var content = new MemoryStream(bytes);
        var stored = await storage.StoreAsync(
            CreateRequest("notes.txt", "text/plain", bytes.Length),
            content);
        await storage.ValidateForPromptAsync(stored.Attachment);
        await File.WriteAllBytesAsync(stored.Attachment.ServerPath, "changed bytes"u8.ToArray());

        var changed = await Assert.ThrowsAsync<HostOperationException>(() =>
            storage.ValidateForPromptAsync(stored.Attachment));

        Assert.Equal(ProtocolErrorCodes.AttachmentIntegrityFailed, changed.Code);
    }

    [Fact]
    public async Task VerifiedDownloadsRejectOutsideStorageAndKeepPromptMissingFileErrorsStable()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions();
        var storage = new DraftAttachmentStorage(options);
        var bytes = "owned attachment"u8.ToArray();
        using var input = new MemoryStream(bytes);
        var stored = await storage.StoreAsync(CreateRequest("notes.txt", "text/plain", bytes.Length), input);
        await using (var file = await storage.OpenVerifiedReadAsync(stored.Attachment))
        {
            Assert.Equal(0, file.Position);
            using var copy = new MemoryStream();
            await file.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        var outside = Path.Combine(directory.CreateDirectory("outside"), "notes.txt");
        await File.WriteAllBytesAsync(outside, bytes);
        var denied = await Assert.ThrowsAsync<HostOperationException>(() => storage.OpenVerifiedReadAsync(
            stored.Attachment with { ServerPath = outside }));
        Assert.Equal(ProtocolErrorCodes.AttachmentInvalid, denied.Code);
        File.Delete(stored.Attachment.ServerPath);
        var missing = await Assert.ThrowsAsync<HostOperationException>(() => storage.ValidateForPromptAsync(stored.Attachment));
        Assert.Equal(ProtocolErrorCodes.AttachmentIntegrityFailed, missing.Code);
    }

    private static UploadDraftAttachmentRequest CreateRequest(
        string fileName,
        string mediaType,
        long byteLength) => new(
        ProtocolVersion.Current,
        EnvironmentId.Parse("environment-1"),
        ClientId.Parse("client-1"),
        CommandId.New(),
        ThreadId.Parse("thread-1"),
        DraftId.Parse("draft-1"),
        AttachmentId.New(),
        0,
        fileName,
        mediaType,
        byteLength);
}
