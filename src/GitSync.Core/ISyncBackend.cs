namespace GitSync.Core;

/// <summary>
/// What the UI needs from a sync engine. Implemented locally by <see cref="SyncService"/> (git runs on this PC)
/// and remotely by <see cref="RemoteSyncClient"/> (git runs on a GitSync server).
/// </summary>
public interface ISyncBackend : IDisposable
{
    bool IsRemote { get; }
    /// <summary>Description of where work runs, for the UI.</summary>
    string Location { get; }
    bool CreateMissingRepos { get; }
    bool SyncTags { get; }
    int MaxParallel { get; }

    Task<string> TestGitHubAsync(CancellationToken ct = default);
    Task<string> TestGitLabAsync(CancellationToken ct = default);

    /// <summary>Lists and pairs all repositories. Remote backends return pairs that are already API-compared.</summary>
    Task<List<RepoPair>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task QuickCompareAsync(RepoPair pair, CancellationToken ct = default);
    Task AnalyzeAsync(RepoPair pair, IProgress<string>? progress = null, CancellationToken ct = default, Provider? fetchOnly = null);
    Task<SyncResult> SyncAsync(RepoPair pair, Provider from, IProgress<string>? progress = null, CancellationToken ct = default);
    Task PushBranchAsync(RepoPair pair, string branch, Provider from, bool force, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Bulk operations. Remote backends run them as a single server job.</summary>
    Task AnalyzeManyAsync(IReadOnlyList<RepoPair> pairs, IProgress<(string Message, double Percent)>? progress = null, CancellationToken ct = default);
    Task<List<SyncResult>> SyncManyAsync(IReadOnlyList<RepoPair> pairs, Provider from, IProgress<(string Message, double Percent)>? progress = null, CancellationToken ct = default);
}
