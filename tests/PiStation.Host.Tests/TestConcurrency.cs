// Keep process-heavy fixtures bounded when the solution runs several test assemblies.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 4)]
