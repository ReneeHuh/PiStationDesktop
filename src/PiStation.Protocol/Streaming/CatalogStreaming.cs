using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Protocol.Streaming;

public sealed record CatalogCursor(string Epoch, long Sequence);

// A transfer is committed by the client only when Complete arrives. Every page is bounded.
public sealed record CatalogBatch(
    EnvironmentId EnvironmentId, string Epoch, long Sequence, bool Reset, bool Complete,
    ProjectDescriptor[] Projects, ThreadDescriptor[] Threads,
    ProjectId[] RemovedProjects, ThreadId[] RemovedThreads);
