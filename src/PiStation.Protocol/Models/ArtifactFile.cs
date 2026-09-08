namespace PiStation.Protocol.Models;

public sealed record ReadArtifactFileRequest(WorkspaceTarget Target, string AbsolutePath);

public sealed record ReadArtifactFileResult(string AbsolutePath, byte[] Content, long ByteLength, string MediaType, bool IsTruncated, bool IsBinary);
