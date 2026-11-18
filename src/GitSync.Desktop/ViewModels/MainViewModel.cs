using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using GitSync.Core;
using GitSync.Desktop.Infrastructure;
using GitSync.Desktop.Views;

namespace GitSync.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _bulkCts;

    public SyncLogger Logger { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<RepoPairViewModel> Pairs { get; } = new();
    public ICollectionView PairsView { get; }
    public SettingsViewModel Settings { get; }
    public SyncService? Service { get; private set; }

    public MainViewModel()
    {
        Logger.Logged += e => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Logs.Insert(0, e);
            while (Logs.Count > 2000) Logs.RemoveAt(Logs.Count - 1);
            if (e.Level == SyncLogLevel.Error) OnPropertyChanged(nameof(ErrorLogCount));
        });

        PairsView = CollectionViewSource.GetDefaultView(Pairs);
        PairsView.Filter = o => o is RepoPairViewModel p && MatchesFilter(p);

        Settings = new SettingsViewModel(this);
        _toastTimer.Tick += (_, _) => { ToastVisible = false; _toastTimer.Stop(); };

        NavigateCommand = new RelayCommand(p => CurrentPage = p as string ?? "Repos");
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        AnalyzeAllCommand = new AsyncRelayCommand(AnalyzeAllAsync, () => !IsBusy && Pairs.Count > 0);
        SyncAllToGitLabCommand = new AsyncRelayCommand(() => SyncAllAsync(Provider.GitHub), () => !IsBusy && Pairs.Count > 0);
        SyncAllToGitHubCommand = new AsyncRelayCommand(() => SyncAllAsync(Provider.GitLab), () => !IsBusy && Pairs.Count > 0);
        CancelCommand = new RelayCommand(() => _bulkCts?.Cancel(), () => _bulkCts is not null);
        ClearLogCommand = new RelayCommand(() => { Logs.Clear(); Logger.Clear(); OnPropertyChanged(nameof(ErrorLogCount)); });
        SetFilterCommand = new RelayCommand(p => Filter = p as string ?? "All");
        DismissToastCommand = new RelayCommand(() => ToastVisible = false);

        AsyncRelayCommand.UnhandledError = ex =>
        {
            Logger.Error(ex.Message, "app", (ex as SyncException)?.Detail);
            ShowToast(ex.Message, "Error");
        };
    }

    // ───────────────────────── commands ─────────────────────────

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AnalyzeAllCommand { get; }
    public AsyncRelayCommand SyncAllToGitLabCommand { get; }
    public AsyncRelayCommand SyncAllToGitHubCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public RelayCommand DismissToastCommand { get; }

    // ───────────────────────── state ─────────────────────────

    private string _currentPage = "Repos";
    public string CurrentPage { get => _currentPage; set => Set(ref _currentPage, value); }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) PairsView.Refresh(); }
    }

    private string _filter = "All";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) PairsView.Refresh(); }
    }

    private RepoPairViewModel? _selectedPair;
    public RepoPairViewModel? SelectedPair { get => _selectedPair; set => Set(ref _selectedPair, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }

    private string _busyMessage = "준비됨";
    public string BusyMessage { get => _busyMessage; private set => Set(ref _busyMessage, value); }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private bool _hasProgress;
    public bool HasProgress { get => _hasProgress; private set => Set(ref _hasProgress, value); }

    private string? _gitHubUser;
    public string? GitHubUser { get => _gitHubUser; private set => Set(ref _gitHubUser, value); }

    private string? _gitLabUser;
    public string? GitLabUser { get => _gitLabUser; private set => Set(ref _gitLabUser, value); }

    private string _lastScanText = "아직 스캔하지 않음";
    public string LastScanText { get => _lastScanText; private set => Set(ref _lastScanText, value); }

    public string GitHubHost => Host(Settings.GitHubApiUrl, "github.com");
    public string GitLabHost => Host(Settings.GitLabUrl, "gitlab.com");

    public int TotalCount => Pairs.Count;
    public int SyncedCount => Pairs.Count(p => Category(p) == "Synced");
    public int OutOfSyncCount => Pairs.Count(p => Category(p) == "OutOfSync");
    public int GitHubOnlyCount => Pairs.Count(p => Category(p) == "GitHubOnly");
    public int GitLabOnlyCount => Pairs.Count(p => Category(p) == "GitLabOnly");
    public int ErrorLogCount => Logs.Count(l => l.Level == SyncLogLevel.Error);
    public bool HasPairs => Pairs.Count > 0;
    public bool IsConfigured => Settings.IsConfigured;

    // toast
    private string _toastMessage = "";
    public string ToastMessage { get => _toastMessage; private set => Set(ref _toastMessage, value); }
    private string _toastKind = "Info";
    public string ToastKind { get => _toastKind; private set => Set(ref _toastKind, value); }
    private bool _toastVisible;
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

    public void ShowToast(string message, string kind = "Info")
    {
        ToastMessage = message;
        ToastKind = kind;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ───────────────────────── lifecycle ─────────────────────────

    public async Task InitializeAsync()
    {
        ApplySettings(Settings.ToOptions(), refresh: false);
        Logger.Info("GitSync 시작", "app");
        if (Settings.IsConfigured) await RefreshAsync();
        else
        {
            CurrentPage = "Settings";
            ShowToast("먼저 GitHub와 GitLab 접근 토큰을 설정하세요.", "Warning");
        }
    }

    public void ApplySettings(SyncOptions options, bool refresh)
    {
        Service?.Dispose();
        Service = new SyncService(options, Logger);
        OnPropertyChanged(nameof(GitHubHost));
        OnPropertyChanged(nameof(GitLabHost));
        OnPropertyChanged(nameof(IsConfigured));
        if (refresh) RefreshCommand.Execute(null);
    }

    // ───────────────────────── operations ─────────────────────────

    private async Task RefreshAsync()
    {
        if (Service is null || !Settings.IsConfigured)
        {
            CurrentPage = "Settings";
            ShowToast("GitHub와 GitLab 토큰을 모두 설정해야 합니다.", "Warning");
            return;
        }
        var svc = Service;
        IsBusy = true;
        try
        {
            BusyMessage = "계정 확인 중…";
            var u1 = svc.TestGitHubAsync();
            var u2 = svc.TestGitLabAsync();
            try { GitHubUser = await u1; } catch { GitHubUser = null; throw; }
            try { GitLabUser = await u2; } catch { GitLabUser = null; throw; }

            BusyMessage = "모든 저장소 검색 중…";
            var pairs = await svc.ScanAsync();
            var selectedId = SelectedPair?.Id;
            Pairs.Clear();
            foreach (var p in pairs) Pairs.Add(new RepoPairViewModel(this, p));
            SelectedPair = Pairs.FirstOrDefault(p => p.Id == selectedId) ?? PairsView.Cast<RepoPairViewModel>().FirstOrDefault();
            UpdateStats();

            var both = Pairs.Where(p => p.HasBoth).ToList();
            await RunBatchAsync(both, 6, "브랜치 비교", p => p.QuickCompareAsync(), CancellationToken.None);
            LastScanText = $"마지막 스캔 {DateTime.Now:HH:mm:ss}";
            ShowToast($"{Pairs.Count}개 저장소 검색 완료 · 차이 {OutOfSyncCount} · GitHub만 {GitHubOnlyCount} · GitLab만 {GitLabOnlyCount}", "Success");
        }
        catch (Exception ex)
        {
            Logger.Error(ex.Message, "scan", (ex as SyncException)?.Detail);
            ShowToast(ex.Message, "Error");
        }
        finally
        {
            IsBusy = false;
            BusyMessage = "준비됨";
            HasProgress = false;
        }
    }

    private async Task AnalyzeAllAsync()
    {
        var targets = VisiblePairs().Where(p => p.HasBoth).ToList();
        if (targets.Count == 0) { ShowToast("분석할 저장소가 없습니다 (양쪽에 모두 있는 저장소만 분석).", "Warning"); return; }
        await RunBulkAsync("정밀 분석", targets, p => p.AnalyzeAsync(showToast: false));
        ShowToast($"{targets.Count}개 저장소 분석 완료", "Success");
    }

    private async Task SyncAllAsync(Provider from)
    {
        var to = from.Other();
        var targets = VisiblePairs().Where(p => p.Model.Get(from) is not null).ToList();
        if (targets.Count == 0) { ShowToast($"{from.Label()} 쪽 저장소가 없습니다.", "Warning"); return; }

        var create = targets.Count(p => p.Model.Get(to) is null);
        var details = new List<string>
        {
            $"대상 저장소: {targets.Count}개 (현재 목록 기준)",
            create > 0
                ? (Settings.CreateMissingRepos ? $"{to.Label()}에 새로 생성: {create}개" : $"{to.Label()}에 없는 {create}개는 자동 생성이 꺼져 있어 건너뜀")
                : $"{to.Label()}에 없는 저장소 없음",
            "새 브랜치 생성 + fast-forward 가능한 브랜치 업데이트",
            Settings.SyncTags ? "누락된 태그 푸시" : "태그는 동기화하지 않음",
            "분기된 브랜치는 덮어쓰지 않고 건너뜀",
        };
        if (!Dialogs.Confirm($"{from.Label()} → {to.Label()} 일괄 동기화",
                $"현재 목록의 저장소를 {to.Label()}로 동기화합니다.", "동기화 시작", false, details))
            return;

        var results = new List<SyncResult>();
        await RunBulkAsync($"{to.Label()}로 동기화", targets, async p =>
        {
            var r = await p.SyncAsync(from, interactive: false);
            if (r is not null) lock (results) results.Add(r);
        });

        var created = results.Count(r => r.CreatedRepo);
        var pushed = results.Sum(r => r.Pushed);
        var failed = results.Count(r => !r.Ok);
        var msg = $"일괄 동기화 완료 · 생성 {created} · 브랜치 푸시 {pushed} · 실패 {failed}";
        Logger.Write(failed == 0 ? SyncLogLevel.Success : SyncLogLevel.Warning, msg, "bulk");
        ShowToast(msg, failed == 0 ? "Success" : "Warning");
    }

    private async Task RunBulkAsync(string label, IReadOnlyList<RepoPairViewModel> items, Func<RepoPairViewModel, Task> work)
    {
        IsBusy = true;
        _bulkCts = new CancellationTokenSource();
        try
        {
            await RunBatchAsync(items, Math.Clamp(Settings.MaxParallel, 1, 8), label, work, _bulkCts.Token);
        }
        finally
        {
            _bulkCts.Dispose();
            _bulkCts = null;
            IsBusy = false;
            HasProgress = false;
            BusyMessage = "준비됨";
            UpdateStats();
        }
    }

    private async Task RunBatchAsync(IReadOnlyList<RepoPairViewModel> items, int parallel, string label,
        Func<RepoPairViewModel, Task> work, CancellationToken ct)
    {
        if (items.Count == 0) return;
        int done = 0;
        HasProgress = true;
        Progress = 0;
        BusyMessage = $"{label} 0/{items.Count}";
        using var gate = new SemaphoreSlim(parallel);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                await work(item);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Error(ex.Message, item.Name, (ex as SyncException)?.Detail); }
            finally
            {
                gate.Release();
                done++;
                Progress = done * 100.0 / items.Count;
                BusyMessage = $"{label} {done}/{items.Count}";
            }
        });
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { ShowToast("작업이 취소되었습니다.", "Warning"); }
    }

    /// <summary>Sample data for UI preview (--demo). Nothing is contacted.</summary>
    public void LoadDemo()
    {
        ApplySettings(Settings.ToOptions(), refresh: false);
        GitHubUser = "demo-user";
        GitLabUser = "demo-user";
        var now = DateTimeOffset.Now;
        RemoteRepo Gh(string n, bool priv = true) => new(Provider.GitHub, 1, n, $"demo-user/{n}", $"https://github.com/demo-user/{n}",
            "", "main", now.AddHours(-3), priv, "샘플 저장소 설명");
        RemoteRepo Gl(string n) => new(Provider.GitLab, 2, n, $"mirrors/{n}", $"https://gitlab.example.com/mirrors/{n}", "", "main", now, true, null);
        CommitInfo C(string sha, string who, double hours, string msg) => new(sha + "a1b2c3d4e5f6", who, now.AddHours(-hours), msg);

        var api = new RepoPair { Id = "1", Name = "payment-api", GitHub = Gh("payment-api"), GitLab = Gl("payment-api"), IsAnalyzed = true, LastChecked = now.AddMinutes(-4) };
        api.Branches =
        [
            new BranchState { Name = "main", IsDefault = true, GitHubSha = "9f3c2e1aa", GitLabSha = "71bd09c4e", GitHubCommit = C("9f3c2e1", "jkim", 2, "Fix refund rounding"),
                GitLabCommit = C("71bd09c", "jkim", 26, "Bump deps"), GitHubAhead = 3, Status = BranchStatus.GitHubAhead },
            new BranchState { Name = "feature/webhooks-v2", GitHubSha = "ab12cd34e", GitHubCommit = C("ab12cd3", "slee", 5, "Add retry queue"), Status = BranchStatus.GitHubOnly },
            new BranchState { Name = "hotfix/timeout", GitHubSha = "5e6f7a8b9", GitLabSha = "0c1d2e3f4", GitHubCommit = C("5e6f7a8", "pyoon", 30, "Raise timeout"),
                GitLabCommit = C("0c1d2e3", "ops-bot", 12, "Hotfix applied on GitLab"), GitHubAhead = 1, GitLabAhead = 2, Status = BranchStatus.Diverged },
            new BranchState { Name = "release/2.4", GitHubSha = "77aa88bb9", GitLabSha = "77aa88bb9", GitHubCommit = C("77aa88b", "jkim", 120, "Release 2.4.0"),
                GitLabCommit = C("77aa88b", "jkim", 120, "Release 2.4.0"), Status = BranchStatus.Synced },
            new BranchState { Name = "develop", GitHubSha = "c0ffee123", GitLabSha = "c0ffee123", GitHubCommit = C("c0ffee1", "slee", 8, "Merge PR #212"),
                GitLabCommit = C("c0ffee1", "slee", 8, "Merge PR #212"), Status = BranchStatus.Synced },
        ];
        api.Tags = new TagSummary(14, 2, 0, 0);
        SyncService.UpdateStatus(api);

        var web = new RepoPair { Id = "2", Name = "web-frontend", GitHub = Gh("web-frontend", false), GitLab = Gl("web-frontend"), LastChecked = now.AddMinutes(-4) };
        web.Branches = [new BranchState { Name = "main", IsDefault = true, GitHubSha = "1", GitLabSha = "1", Status = BranchStatus.Synced }];
        SyncService.UpdateStatus(web);
        var infra = new RepoPair { Id = "3", Name = "infra-terraform", GitHub = Gh("infra-terraform"), LastChecked = now };
        SyncService.UpdateStatus(infra);
        var docs = new RepoPair { Id = "4", Name = "internal-docs", GitLab = Gl("internal-docs"), LastChecked = now };
        SyncService.UpdateStatus(docs);
        var auth = new RepoPair { Id = "5", Name = "auth-service", GitHub = Gh("auth-service"), GitLab = Gl("auth-service"), LastChecked = now.AddMinutes(-4) };
        auth.Branches = [new BranchState { Name = "main", IsDefault = true, GitHubSha = "1", GitLabSha = "2", Status = BranchStatus.Differs }];
        SyncService.UpdateStatus(auth);

        Pairs.Clear();
        foreach (var p in new[] { auth, infra, docs, api, web }) Pairs.Add(new RepoPairViewModel(this, p));
        SelectedPair = Pairs.First(p => p.Name == "payment-api");
        LastScanText = $"마지막 스캔 {DateTime.Now:HH:mm:ss}";
        Logger.Info("저장소 목록을 조회합니다…", "scan");
        Logger.Success("GitHub 4개, GitLab 4개 → 5개 항목 (매칭 3, GitHub만 1, GitLab만 1)", "scan");
        Logger.Command("git fetch https://github.com/demo-user/payment-api.git", "payment-api");
        Logger.Error("'hotfix/timeout' 푸시 실패", "payment-api", " ! [rejected] hotfix/timeout -> hotfix/timeout (non-fast-forward)");
        UpdateStats();
        ShowToast("5개 저장소 검색 완료 · 차이 2 · GitHub만 1 · GitLab만 1", "Success");
    }

    // ───────────────────────── helpers ─────────────────────────

    public IEnumerable<RepoPairViewModel> VisiblePairs() => PairsView.Cast<RepoPairViewModel>();

    public void UpdateStats()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(SyncedCount));
        OnPropertyChanged(nameof(OutOfSyncCount));
        OnPropertyChanged(nameof(GitHubOnlyCount));
        OnPropertyChanged(nameof(GitLabOnlyCount));
        OnPropertyChanged(nameof(HasPairs));
        if (Filter != "All") PairsView.Refresh();
    }

    private static string Category(RepoPairViewModel p) => p.Status switch
    {
        RepoStatus.Synced or RepoStatus.Empty => "Synced",
        RepoStatus.GitHubOnly => "GitHubOnly",
        RepoStatus.GitLabOnly => "GitLabOnly",
        _ => "OutOfSync",
    };

    private bool MatchesFilter(RepoPairViewModel p)
    {
        if (Filter != "All" && Category(p) != Filter) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (p.GitHub?.FullName.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || (p.GitLab?.FullName.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string Host(string url, string fallback)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.Host.StartsWith("api.") ? u.Host[4..] : u.Host;
        return fallback;
    }
}
