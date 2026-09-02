using Xunit;

/// <summary>
/// Every test class that assigns <c>CultureInfo.CurrentCulture</c> joins this collection.
/// xUnit runs test classes in parallel on shared pool threads, so without it a concurrent
/// test formatting any number can observe de-DE and fail for a reason that has nothing to do
/// with it - rarely, and miserably to debug. No fixture is needed; the collection exists only
/// to stop the parallelism.
/// </summary>
[CollectionDefinition("culture", DisableParallelization = true)]
public sealed class CultureCollection;
