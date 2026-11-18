using GitSync.Core;
using GitSync.Desktop.Infrastructure;

namespace GitSync.Desktop.ViewModels;

public sealed class BranchRowViewModel
{
    private readonly RepoPairViewModel _pair;
    public BranchState State { get; }

    public BranchRowViewModel(RepoPairViewModel pair, BranchState state)
    {
        _pair = pair;
        State = state;
        PushToGitLabCommand = new AsyncRelayCommand(() => _pair.PushBranchAsync(Name, Provider.GitHub), () => CanPushToGitLab && !_pair.IsWorking);
        PushToGitHubCommand = new AsyncRelayCommand(() => _pair.PushBranchAsync(Name, Provider.GitLab), () => CanPushToGitHub && !_pair.IsWorking);
    }

    public AsyncRelayCommand PushToGitLabCommand { get; }
    public AsyncRelayCommand PushToGitHubCommand { get; }

    public string Name => State.Name;
    public bool IsDefault => State.IsDefault;
    public BranchStatus Status => State.Status;
    public bool HasGitHub => State.GitHubSha is not null;
    public bool HasGitLab => State.GitLabSha is not null;
    public bool CanPushToGitLab => HasGitHub && Status != BranchStatus.Synced;
    public bool CanPushToGitHub => HasGitLab && Status != BranchStatus.Synced;

    public string GitHubShort => Short(State.GitHubSha);
    public string GitLabShort => Short(State.GitLabSha);
    public string GitHubMeta => Meta(State.GitHubCommit);
    public string GitLabMeta => Meta(State.GitLabCommit);
    public string? GitHubSubject => State.GitHubCommit?.Subject;
    public string? GitLabSubject => State.GitLabCommit?.Subject;

    public string AheadText => Status switch
    {
        BranchStatus.GitHubAhead => $"+{State.GitHubAhead} 커밋",
        BranchStatus.GitLabAhead => $"+{State.GitLabAhead} 커밋",
        BranchStatus.Diverged => $"GH +{State.GitHubAhead} · GL +{State.GitLabAhead}",
        BranchStatus.GitHubOnly => "GitLab에 생성 필요",
        BranchStatus.GitLabOnly => "GitHub에 없음",
        BranchStatus.Differs => "SHA 다름",
        _ => "",
    };

    public string PushToGitLabTip => SyncService.RequiresForce(State, Provider.GitHub)
        ? "GitHub → GitLab 강제 푸시 (GitLab 쪽 커밋을 덮어씀)"
        : HasGitLab ? "GitHub → GitLab 푸시" : "GitLab에 브랜치 생성";
    public string PushToGitHubTip => SyncService.RequiresForce(State, Provider.GitLab)
        ? "GitLab → GitHub 강제 푸시 (GitHub 쪽 커밋을 덮어씀)"
        : HasGitHub ? "GitLab → GitHub 푸시" : "GitHub에 브랜치 생성";

    private static string Short(string? sha) => sha is { Length: >= 7 } ? sha[..7] : "—";

    private static string Meta(CommitInfo? c) =>
        c is null ? "" : $"{c.Author} · {(c.Date is { } d ? Relative(d) : "")}";

    public static string Relative(DateTimeOffset d)
    {
        var span = DateTimeOffset.Now - d;
        if (span.TotalSeconds < 60) return "방금 전";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}분 전";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}시간 전";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}일 전";
        return d.LocalDateTime.ToString("yyyy-MM-dd");
    }
}
