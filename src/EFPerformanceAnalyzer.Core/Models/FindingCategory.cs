namespace EFPerformanceAnalyzer.Core.Models;

public enum FindingCategory
{
    // Original EF-model-aware detectors
    NPlusOneQuery,
    MissingAsNoTracking,
    MissingInclude,
    UnusedNavigationProperty,
    MultipleSaveChanges,

    // Query-shape detectors
    ClientSideEvaluation,
    QueryInLoop,
    SaveChangesInLoop,
    UnboundedQuery,
    CartesianInclude,
    InefficientCount,

    // General .NET throughput detectors
    SyncOverAsync,
    AsyncVoid,
    StringConcatInLoop,
    BlockingCallInAsyncMethod
}
