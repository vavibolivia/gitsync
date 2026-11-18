namespace GitSync.Core;

public enum Provider { GitHub, GitLab }

public enum RepoStatus { Unknown, Synced, OutOfSync, GitHubOnly, GitLabOnly, Empty, Error }

public enum BranchStatus { Synced, Differs, GitHubAhead, GitLabAhead, Diverged, GitHubOnly, GitLabOnly }

public sealed record RemoteRepo(
    Provider Provider,
    long Id,
    string Name,
    string FullName,
    string WebUrl,
    string CloneUrl,
    string? DefaultBranch,
    DateTimeOffset? UpdatedAt,
    bool IsPrivate,
    string? Description);

public sealed record RemoteBranch(string Name, string Sha);

public sealed record CommitInfo(string Sha, string Author, DateTimeOffset? Date, string Subject);

public sealed class BranchState
{
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public string? GitHubSha { get; init; }
    public string? GitLabSha { get; init; }
    public CommitInfo? GitHubCommit { get; init; }
    public CommitInfo? GitLabCommit { get; init; }
    /// <summary>Commits present on GitHub but not on GitLab.</summary>
    public int GitHubAhead { get; init; }
    /// <summary>Commits present on GitLab but not on GitHub.</summary>
    public int GitLabAhead { get; init; }
    public BranchStatus Status { get; init; }

    public string? Sha(Provider p) => p == Provider.GitHub ? GitHubSha : GitLabSha;
    public int Ahead(Provider p) => p == Provider.GitHub ? GitHubAhead : GitLabAhead;
}

public sealed record TagSummary(int Synced, int GitHubOnly, int GitLabOnly, int Different)
{
    public int Total => Synced + GitHubOnly + GitLabOnly + Different;
    public bool InSync => GitHubOnly == 0 && GitLabOnly == 0 && Different == 0;
}

/// <summary>A GitHub repository and its GitLab counterpart (either side may be missing).</summary>
public sealed class RepoPair
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public RemoteRepo? GitHub { get; set; }
    public RemoteRepo? GitLab { get; set; }
    public RepoStatus Status { get; set; } = RepoStatus.Unknown;
    /// <summary>Always replaced as a whole, never mutated in place (safe to read concurrently).</summary>
    public IReadOnlyList<BranchState> Branches { get; set; } = Array.Empty<BranchState>();
    public TagSummary? Tags { get; set; }
    public bool IsAnalyzed { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public string? Error { get; set; }

    public bool HasBoth => GitHub is not null && GitLab is not null;
    public RemoteRepo? Get(Provider p) => p == Provider.GitHub ? GitHub : GitLab;
    public void Set(Provider p, RemoteRepo repo) { if (p == Provider.GitHub) GitHub = repo; else GitLab = repo; }
    public int DiffCount => Branches.Count(b => b.Status != BranchStatus.Synced);
    public string? DefaultBranch => GitHub?.DefaultBranch ?? GitLab?.DefaultBranch;
}

public enum BranchActionKind { Create, FastForward, Skip }

public sealed record BranchAction(string Branch, BranchActionKind Kind, int Commits, string? Reason = null);

public sealed class SyncPlan
{
    public required Provider From { get; init; }
    public Provider To => From.Other();
    public List<BranchAction> Actions { get; } = new();
    public IEnumerable<BranchAction> Pushes => Actions.Where(a => a.Kind != BranchActionKind.Skip);
    public IEnumerable<BranchAction> Skipped => Actions.Where(a => a.Kind == BranchActionKind.Skip);
}

public sealed record SyncResult(string Repo, bool CreatedRepo, int Pushed, int Skipped, int Failed, string? Error = null)
{
    public bool Ok => Error is null && Failed == 0;
}

public static class ProviderExtensions
{
    public static Provider Other(this Provider p) => p == Provider.GitHub ? Provider.GitLab : Provider.GitHub;
    public static string Label(this Provider p) => p == Provider.GitHub ? "GitHub" : "GitLab";
    public static string RemoteName(this Provider p) => p == Provider.GitHub ? "github" : "gitlab";
}

public sealed class SyncException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}
