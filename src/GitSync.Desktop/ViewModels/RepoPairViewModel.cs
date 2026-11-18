using System.Collections.ObjectModel;
using System.Diagnostics;
using GitSync.Core;
using GitSync.Desktop.Infrastructure;
using GitSync.Desktop.Views;

namespace GitSync.Desktop.ViewModels;

public sealed class RepoPairViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly Progress<string> _progress;

    public RepoPair Model { get; }
    public ObservableCollection<BranchRowViewModel> Branches { get; } = new();

    public RepoPairViewModel(MainViewModel shell, RepoPair model)
    {
        _shell = shell;
        Model = model;
        _progress = new Progress<string>(m => WorkMessage = m);

        AnalyzeCommand = new AsyncRelayCommand(() => AnalyzeAsync(showToast: true), () => HasBoth && !IsWorking);
        SyncToGitLabCommand = new AsyncRelayCommand(() => SyncAsync(Provider.GitHub, true), () => HasGitHub && !IsWorking);
        SyncToGitHubCommand = new AsyncRelayCommand(() => SyncAsync(Provider.GitLab, true), () => HasGitLab && !IsWorking);
        OpenGitHubCommand = new RelayCommand(() => Open(GitHub?.WebUrl), () => HasGitHub);
        OpenGitLabCommand = new RelayCommand(() => Open(GitLab?.WebUrl), () => HasGitLab);
        RebuildBranches();
    }

    public AsyncRelayCommand AnalyzeCommand { get; }
    public AsyncRelayCommand SyncToGitLabCommand { get; }
    public AsyncRelayCommand SyncToGitHubCommand { get; }
    public RelayCommand OpenGitHubCommand { get; }
    public RelayCommand OpenGitLabCommand { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public RemoteRepo? GitHub => Model.GitHub;
    public RemoteRepo? GitLab => Model.GitLab;
    public bool HasGitHub => Model.GitHub is not null;
    public bool HasGitLab => Model.GitLab is not null;
    public bool HasBoth => Model.HasBoth;
    public bool MissingGitLab => HasGitHub && !HasGitLab;
    public bool MissingGitHub => HasGitLab && !HasGitHub;
    public RepoStatus Status => Model.Status;
    public string? Error => Model.Error;
    public bool IsAnalyzed => Model.IsAnalyzed;
    public string? DefaultBranch => Model.DefaultBranch;
    public string? Description => Model.GitHub?.Description is { Length: > 0 } d ? d : Model.GitLab?.Description;
    public bool IsPrivate => (Model.GitHub ?? Model.GitLab)?.IsPrivate ?? false;
    public string VisibilityText => IsPrivate ? "Private" : "Public";

    public int BranchCount => Model.Branches.Count;
    public int DiffCount => Model.DiffCount;

    public string Subtitle => Status switch
    {
        RepoStatus.GitHubOnly => "GitLab에 없음 · 생성 가능",
        RepoStatus.GitLabOnly => "GitHub에 없음",
        RepoStatus.Error => "오류 발생",
        RepoStatus.Unknown => "비교 대기 중",
        RepoStatus.Empty => "브랜치 없음",
        _ => DiffCount > 0 ? $"브랜치 {BranchCount}개 · {DiffCount}개 다름" : $"브랜치 {BranchCount}개 · 모두 동일",
    };

    public string TagText => Model.Tags is not { } t
        ? (IsAnalyzed ? "" : "태그: 정밀 분석 후 표시")
        : t.Total == 0 ? "태그 없음"
        : $"태그 {t.Total}개 · 동일 {t.Synced}" +
          (t.GitHubOnly > 0 ? $" · GitHub만 {t.GitHubOnly}" : "") +
          (t.GitLabOnly > 0 ? $" · GitLab만 {t.GitLabOnly}" : "") +
          (t.Different > 0 ? $" · 다름 {t.Different}" : "");

    public string LastCheckedText => Model.LastChecked is { } d
        ? $"{(IsAnalyzed ? "정밀 분석" : "API 비교")} · {BranchRowViewModel.Relative(d)}"
        : "아직 비교하지 않음";

    private bool _isWorking;
    public bool IsWorking
    {
        get => _isWorking;
        private set { if (Set(ref _isWorking, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    private string _workMessage = "";
    public string WorkMessage { get => _workMessage; private set => Set(ref _workMessage, value); }

    private ISyncBackend Svc => _shell.Backend ?? throw new SyncException("설정이 완료되지 않았습니다.");

    public void SetWorking(bool working, string message) { IsWorking = working; WorkMessage = message; }

    // ───────────────────────── operations ─────────────────────────

    public async Task QuickCompareAsync()
    {
        IsWorking = true;
        WorkMessage = "브랜치 조회 중…";
        try { await Svc.QuickCompareAsync(Model); }
        finally { IsWorking = false; Refresh(); }
    }

    public async Task AnalyzeAsync(bool showToast)
    {
        IsWorking = true;
        try
        {
            await Svc.AnalyzeAsync(Model, _progress);
            if (showToast)
                _shell.ShowToast(Status == RepoStatus.Synced ? $"{Name}: 모든 브랜치가 동일합니다." : $"{Name}: {DiffCount}개 브랜치에 차이가 있습니다.",
                    Status == RepoStatus.Synced ? "Success" : "Info");
        }
        catch (Exception ex) when (showToast)
        {
            _shell.ShowToast($"{Name}: {ex.Message}", "Error");
        }
        finally { IsWorking = false; Refresh(); }
    }

    public async Task<SyncResult?> SyncAsync(Provider from, bool interactive)
    {
        var to = from.Other();
        IsWorking = true;
        try
        {
            if (interactive)
            {
                var details = new List<string>();
                if (Model.Get(to) is null)
                {
                    if (!Svc.CreateMissingRepos)
                    {
                        _shell.ShowToast($"{to.Label()}에 저장소가 없고, 설정에서 자동 생성이 꺼져 있습니다.", "Warning");
                        return null;
                    }
                    var target = to == Provider.GitLab
                        ? $"{(_shell.Settings.EffectiveNamespace is { Length: > 0 } ns ? ns : _shell.GitLabUser)}/{Model.Get(from)!.Name}"
                        : $"{(_shell.Settings.GitHubOwner is { Length: > 0 } o ? o : _shell.GitHubUser)}/{Model.Get(from)!.Name}";
                    details.Add($"새 저장소: {target}");
                    if (to == Provider.GitLab) details.Add($"공개 범위: {_shell.Settings.GitLabVisibility}");
                    details.Add("모든 브랜치" + (Svc.SyncTags ? "와 태그" : "") + " 복사");
                    if (!Dialogs.Confirm($"{to.Label()}에 저장소 생성", $"'{Name}' 저장소를 {to.Label()}에 만들고 내용을 복사합니다.",
                            "생성 후 동기화", false, details))
                        return null;
                }
                else
                {
                    await Svc.AnalyzeAsync(Model, _progress);
                    Refresh();
                    var plan = SyncService.BuildPlan(Model, from);
                    var missingTags = (from == Provider.GitHub ? Model.Tags?.GitHubOnly : Model.Tags?.GitLabOnly) ?? 0;
                    if (!plan.Pushes.Any() && (!Svc.SyncTags || missingTags == 0))
                    {
                        _shell.ShowToast(plan.Skipped.Any()
                            ? $"자동으로 푸시할 브랜치가 없습니다. 분기된 브랜치 {plan.Skipped.Count()}개는 개별 강제 푸시가 필요합니다."
                            : $"{to.Label()}가 이미 최신입니다.", plan.Skipped.Any() ? "Warning" : "Success");
                        return null;
                    }
                    foreach (var a in plan.Pushes)
                        details.Add(a.Kind == BranchActionKind.Create ? $"＋ {a.Branch}  (새 브랜치)" : $"↑ {a.Branch}  (+{a.Commits} 커밋)");
                    if (Svc.SyncTags && missingTags > 0) details.Add($"＋ 태그 {missingTags}개");
                    foreach (var s in plan.Skipped) details.Add($"⨯ {s.Branch}  건너뜀 — {s.Reason}");
                    if (!Dialogs.Confirm($"{from.Label()} → {to.Label()} 동기화", $"'{Name}'의 다음 변경 사항을 {to.Label()}로 푸시합니다.",
                            "푸시", false, details))
                        return null;
                }
            }

            var result = await Svc.SyncAsync(Model, from, _progress);
            if (interactive)
            {
                if (result.Error is not null) _shell.ShowToast($"{Name}: {result.Error}", "Error");
                else _shell.ShowToast($"{Name}: {(result.CreatedRepo ? "저장소 생성 · " : "")}브랜치 {result.Pushed}개 푸시" +
                                      (result.Failed > 0 ? $" · 실패 {result.Failed}" : ""), result.Failed > 0 ? "Warning" : "Success");
            }
            return result;
        }
        finally
        {
            IsWorking = false;
            Refresh();
        }
    }

    public async Task PushBranchAsync(string branch, Provider from)
    {
        var to = from.Other();
        IsWorking = true;
        try
        {
            if (!Model.IsAnalyzed || Model.Branches.FirstOrDefault(b => b.Name == branch)?.Status == BranchStatus.Differs)
                await Svc.AnalyzeAsync(Model, _progress);

            var state = Model.Branches.FirstOrDefault(b => b.Name == branch);
            if (state?.Sha(from) is null) { _shell.ShowToast($"{from.Label()}에 '{branch}' 브랜치가 없습니다.", "Warning"); return; }
            if (state.Status == BranchStatus.Synced) { _shell.ShowToast($"'{branch}'는 이미 동일합니다.", "Success"); return; }

            var force = SyncService.RequiresForce(state, from);
            if (force && !Dialogs.Confirm("강제 푸시", $"{to.Label()}의 '{branch}' 브랜치를 {from.Label()} 내용으로 덮어씁니다.",
                    "강제 푸시", true,
                    [
                        $"{to.Label()}에만 있는 커밋 {state.Ahead(to)}개가 브랜치에서 사라집니다.",
                        $"{from.Label()}에만 있는 커밋 {state.Ahead(from)}개가 반영됩니다.",
                        "보호된 브랜치는 서버에서 거부될 수 있습니다.",
                    ]))
                return;

            await Svc.PushBranchAsync(Model, branch, from, force, _progress);
            _shell.ShowToast($"'{branch}' → {to.Label()} 푸시 완료", "Success");
        }
        catch (Exception ex)
        {
            _shell.Logger.Error(ex.Message, Name, (ex as SyncException)?.Detail);
            _shell.ShowToast(ex.Message, "Error");
        }
        finally
        {
            IsWorking = false;
            Refresh();
        }
    }

    // ───────────────────────── helpers ─────────────────────────

    public void Refresh()
    {
        RebuildBranches();
        foreach (var n in new[]
                 {
                     nameof(GitHub), nameof(GitLab), nameof(HasGitHub), nameof(HasGitLab), nameof(HasBoth),
                     nameof(MissingGitHub), nameof(MissingGitLab), nameof(Status), nameof(Error), nameof(IsAnalyzed),
                     nameof(DefaultBranch), nameof(Description), nameof(IsPrivate), nameof(VisibilityText),
                     nameof(BranchCount), nameof(DiffCount), nameof(Subtitle), nameof(TagText), nameof(LastCheckedText),
                 })
            OnPropertyChanged(n);
        _shell.UpdateStats();
    }

    private void RebuildBranches()
    {
        Branches.Clear();
        foreach (var b in Model.Branches) Branches.Add(new BranchRowViewModel(this, b));
    }

    private static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
    }
}
