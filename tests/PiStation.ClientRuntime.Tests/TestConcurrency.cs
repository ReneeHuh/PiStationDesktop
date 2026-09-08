// Integration tests launch real servers, Git and Pi processes. Bound resource contention
// across independent fixtures; concurrency inside each multi-device test is unchanged.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
