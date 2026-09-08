using System.Text;
using PiStation.Host.Files;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class ArtifactFileTests
{
    [Fact]
    public async Task ReadsReportOutsideWorkspaceWithoutChangingIt()
    {
        using var directory = new HostTestDirectory();
        var file = directory.GetPath("report.html");
        const string html = "<h1>Report</h1><script src='./neighbor.js'></script>";
        await File.WriteAllTextAsync(file, html);
        var before = File.GetLastWriteTimeUtc(file);
        var result = await ArtifactFileService.ReadAsync(file);
        Assert.Equal("text/html", result.MediaType);
        Assert.Equal(html, Encoding.UTF8.GetString(result.Content));
        Assert.Equal(before, File.GetLastWriteTimeUtc(file));
        Assert.False(result.IsBinary);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task TextIsBoundedAndUnknownBinaryIsNotDecoded()
    {
        using var directory = new HostTestDirectory();
        var file = directory.GetPath("large.txt");
        await File.WriteAllTextAsync(file, new string('x', FileReadDefaults.MaximumBytes + 50));
        var result = await ArtifactFileService.ReadAsync(file);
        Assert.True(result.IsTruncated);
        Assert.Equal(FileReadDefaults.MaximumBytes, result.Content.Length);
        var binary = directory.GetPath("binary.dat");
        await File.WriteAllBytesAsync(binary, [1, 0, 2]);
        var unsupported = await ArtifactFileService.ReadAsync(binary);
        Assert.True(unsupported.IsBinary);
        Assert.Empty(unsupported.Content);
    }

    [Fact]
    public async Task PdfBytesAreReturnedAndOversizedAssetsAreRejected()
    {
        using var directory = new HostTestDirectory();
        var file = directory.GetPath("report.pdf");
        await File.WriteAllBytesAsync(file, Encoding.ASCII.GetBytes("%PDF-1.7\n"));
        var result = await ArtifactFileService.ReadAsync(file);
        Assert.Equal("application/pdf", result.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(file), result.Content);
        await using (var stream = File.OpenWrite(file)) stream.SetLength(FileAssetDefaults.MaximumBytes + 1L);
        await Assert.ThrowsAsync<IOException>(() => ArtifactFileService.ReadAsync(file));
    }

    [Fact]
    public async Task MissingDirectoriesRelativeAndDevicePathsAreRejected()
    {
        using var directory = new HostTestDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() => ArtifactFileService.ReadAsync(directory.GetPath("missing.txt")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ArtifactFileService.ReadAsync(directory.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => ArtifactFileService.ReadAsync("../relative.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => ArtifactFileService.ReadAsync("\\\\.\\pipe\\anything"));
    }
}
