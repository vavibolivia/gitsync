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
    public ISyncBackend? Backend { get; private set; }
    public bool IsServerMode => Backend?.IsRemote == true;
    public string ExecutionLocation => Backend is { IsRemote: true } b ? $"서버 · {b.Location}" : "이 PC";
    private readonly DispatcherTimer _remoteLogTimer = new() { Interval = TimeSpan.FromSeconds(5) };

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
        _remoteLogTimer.Tick += async (_, _) =>
        {
            if (Backend is RemoteSyncClient r && !IsBusy)
                try { await r.PullLogsAsync(); } catch { /* server unreachable: shown on next action */ }
        };

        NavigateCommand = new RelayCommand(p => CurrentPage = p as string ?? "Repos");
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        AnalyzeAllCommand = new AsyncRelayCommand(AnalyzeAllAsync, () => !IsBusy && Pairs.Count > 0);
        SyncAllToGitLabCommand = new AsyncRelayCommand(() => SyncAllAsync(Provider.GitHub), () => !IsBusy && Pairs.Count > 0);
        SyncAllToGitHubCommand = new AsyncRelayCommand(() => SyncAllAsync(Provider.GitLab), () => !IsBusy && Pairs.Count > 0);
        CancelCommand = new RelayCommand(() => _bulkCts?.Cancel(), () => _bulkCts is not null);
        ClearLogCommand = new RelayCommand(() => { Logs.Clear(); Logger.Clear(); OnPropertyChanged(nameof(ErrorLogCount)); });
        SetFilterCommand = new RelayCommand(p => Filter = p as string ?? "All");
        DismissToastCommand = new RelayCommand(() => ToastVisible = false);
        AddPairCommand = new RelayCommand(AddPair, () => !IsBusy && CanEditPairs &&
            (IsRealChoice(SelectedGitHubChoice) || IsRealChoice(SelectedGitLabChoice)));
        RemovePairCommand = new RelayCommand(RemovePair, () => !IsBusy && CanEditPairs && SelectedPair is not null);

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
    public RelayCommand AddPairCommand { get; }
    public RelayCommand RemovePairCommand { get; }

    // ───────────────────────── hand-made pairs (local mode) ─────────────────────────

    public const string NewRepoChoice = "(같은 이름으로 새로 생성)";
    public ObservableCollection<string> GitHubChoices { get; } = new();
    public ObservableCollection<string> GitLabChoices { get; } = new();

    private string? _selectedGitHubChoice, _selectedGitLabChoice;
    public string? SelectedGitHubChoice { get => _selectedGitHubChoice; set => Set(ref _selectedGitHubChoice, value); }
    public string? SelectedGitLabChoice { get => _selectedGitLabChoice; set => Set(ref _selectedGitLabChoice, value); }

    /// <summary>Pairs are added by hand only when git runs on this PC; a server scans everything itself.</summary>
    public bool CanEditPairs => Backend is { IsRemote: false };

    private static bool IsRealChoice(string? choice) => !string.IsNullOrEmpty(choice) && choice != NewRepoChoice;

    private void LoadChoices(SyncService svc)
    {
        void Fill(ObservableCollection<string> target, IEnumerable<RemoteRepo> repos, Func<RepoPairViewModel, RemoteRepo?> side)
        {
            var paired = Pairs.Select(p => side(p)?.FullName).Where(n => n is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            target.Clear();
            target.Add(NewRepoChoice);
            foreach (var name in repos.Select(r => r.FullName).Where(n => !paired.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                target.Add(name);
        }
        Fill(GitHubChoices, svc.LastGitHubRepos, p => p.Model.GitHub);
        Fill(GitLabChoices, svc.LastGitLabRepos, p => p.Model.GitLab);
        SelectedGitHubChoice = SelectedGitLabChoice = NewRepoChoice;
    }

    private void AddPair()
    {
        var gh = IsRealChoice(SelectedGitHubChoice) ? SelectedGitHubChoice! : "";
        var gl = IsRealChoice(SelectedGitLabChoice) ? SelectedGitLabChoice! : "";
        if (gh.Length == 0 && gl.Length == 0) return;
        var line = $"{gh}={gl}";
        var lines = Settings.Pairs.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (!lines.Contains(line, StringComparer.OrdinalIgnoreCase)) lines.Add(line);
        Settings.SavePairs(string.Join('\n', lines));
        Logger.Info($"저장소 쌍 추가: {(gh.Length > 0 ? gh : "(새로 생성)")} ⇄ {(gl.Length > 0 ? gl : "(새로 생성)")}", "pairs");
        ApplySettings(refresh: true);
    }

    private void RemovePair()
    {
        if (SelectedPair?.Model is not { } pair) return;
        static bool Same(string key, RemoteRepo? repo) => repo is not null &&
            (key.Equals(repo.FullName, StringComparison.OrdinalIgnoreCase) ||
             (!key.Contains('/') && key.Equals(repo.Name, StringComparison.OrdinalIgnoreCase)));
        var kept = Settings.Pairs.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Where(l =>
        {
            var i = l.IndexOf('=');
            if (i < 0) return false;
            var (gh, gl) = (l[..i].Trim(), l[(i + 1)..].Trim());
            return !(gh.Length > 0 ? Same(gh, pair.GitHub) : Same(gl, pair.GitLab));
        });
        Settings.SavePairs(string.Join('\n', kept));
        Logger.Info($"저장소 쌍 제거: {pair.Name} (원격 저장소는 삭제하지 않음)", "pairs");
        ApplySettings(refresh: true);
    }

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

    public string GitHubHost => Host(SyncOptions.NormalizeGitHubApiUrl(Settings.GitHubApiUrl), "github.com");
    public string GitLabHost => Host(SyncOptions.NormalizeGitLabUrl(Settings.GitLabUrl), "gitlab.com");

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

    // ───────────────────────── self-update ─────────────────────────

    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(3) };
    private bool _updating;

    public string VersionText => $"v{AutoUpdater.CurrentVersion}";

    private void StartAutoUpdate()
    {
        AutoUpdater.CleanupOld();
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--updated-to");
        if (i >= 0 && i + 1 < args.Length)
        {
            Logger.Success($"{args[i + 1]}(으)로 업데이트되었습니다. 설정과 저장소 쌍은 그대로 유지됩니다.", "update");
            ShowToast($"{args[i + 1]}(으)로 업데이트되었습니다.", "Success");
        }
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        _ = CheckForUpdateAsync();
    }

    /// <summary>Installs a newer release when there is one. Never interrupts a running job or unsaved settings.</summary>
    private async Task CheckForUpdateAsync()
    {
        if (_updating || !Settings.AutoUpdate || !AutoUpdater.CanSelfUpdate) return;
        _updating = true;
        try
        {
            var info = await AutoUpdater.CheckAsync();
            if (info is null) return;
            Logger.Info($"새 버전 {info.Tag} 발견 (현재 {VersionText}). 내려받는 중…", "update");
            var file = await AutoUpdater.DownloadAsync(info);
            Logger.Info($"{info.Tag} 내려받기 및 검증 완료", "update");

            while (IsBusy || Settings.IsDirty) await Task.Delay(2000);   // wait for a quiet moment
            if (!Settings.AutoUpdate) { try { System.IO.File.Delete(file); } catch { /* ignore */ } return; }

            ShowToast($"{info.Tag}(으)로 업데이트합니다. 잠시 후 다시 시작됩니다.", "Info");
            await Task.Delay(1500);
            SettingsStore.Backup();
            AutoUpdater.ApplyAndRestart(file, info);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Warn($"자동 업데이트 실패: {ex.Message}", "update");
        }
        finally
        {
            _updating = false;
        }
    }

    public async Task InitializeAsync()
    {
        StartAutoUpdate();
        ApplySettings(refresh: false);
        Logger.Info($"GitSync 시작 · 실행 위치: {ExecutionLocation}", "app");
        if (Settings.IsConfigured) await RefreshAsync();
        else
        {
            CurrentPage = "Settings";
            ShowToast("먼저 설정에서 GitHub와 GitLab의 ID와 키를 입력하세요.", "Warning");
        }
    }

    public void ApplySettings(bool refresh)
    {
        Backend?.Dispose();
        Backend = null;
        _remoteLogTimer.Stop();
        var s = Settings.ToDesktopSettings();
        try
        {
            if (s.IsServer)
            {
                Backend = new RemoteSyncClient(s.ServerUrl, s.ServerKey, Logger);
                _remoteLogTimer.Start();
            }
            else Backend = new SyncService(s.Options, Logger);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, "Error");
            refresh = false;
        }
        Pairs.Clear();
        SelectedPair = null;
        GitHubUser = GitLabUser = null;
        UpdateStats();
        OnPropertyChanged(nameof(IsServerMode));
        OnPropertyChanged(nameof(CanEditPairs));
        OnPropertyChanged(nameof(ExecutionLocation));
        OnPropertyChanged(nameof(GitHubHost));
        OnPropertyChanged(nameof(GitLabHost));
        OnPropertyChanged(nameof(IsConfigured));
        if (refresh) RefreshCommand.Execute(null);
    }

    // ───────────────────────── operations ─────────────────────────

    private async Task RefreshAsync()
    {
        if (Backend is null || !Settings.IsConfigured)
        {
            CurrentPage = "Settings";
            ShowToast(Settings.IsServerMode ? "GitSync 서버 주소를 설정해야 합니다." : "GitHub와 GitLab 키를 모두 설정해야 합니다.", "Warning");
            return;
        }
        var svc = Backend;
        IsBusy = true;
        try
        {
            BusyMessage = "계정 확인 중…";
            var u1 = svc.TestGitHubAsync();
            var u2 = svc.TestGitLabAsync();
            try { GitHubUser = await u1; } catch { GitHubUser = null; throw; }
            try { GitLabUser = await u2; } catch { GitLabUser = null; throw; }

            BusyMessage = svc.IsRemote ? $"서버({svc.Location})에서 모든 저장소 검색 중…" : "저장소 목록 불러오는 중…";
            var pairs = await svc.ScanAsync(new Progress<string>(m => BusyMessage = m));
            if (svc is RemoteSyncClient rc)
            {
                await Settings.LoadFromServerAsync(rc);
                OnPropertyChanged(nameof(GitHubHost));
                OnPropertyChanged(nameof(GitLabHost));
            }
            var selectedId = SelectedPair?.Id;
            Pairs.Clear();
            foreach (var p in pairs) Pairs.Add(new RepoPairViewModel(this, p));
            SelectedPair = Pairs.FirstOrDefault(p => p.Id == selectedId) ?? PairsView.Cast<RepoPairViewModel>().FirstOrDefault();
            UpdateStats();
            if (svc is SyncService local) LoadChoices(local);

            if (!svc.IsRemote)   // the server already compared during its scan job
            {
                var both = Pairs.Where(p => p.HasBoth).ToList();
                await RunBatchAsync(both, 6, "브랜치 비교", p => p.QuickCompareAsync(), CancellationToken.None);
            }
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
        if (Backend is { IsRemote: true } remote)
            await RunRemoteBulkAsync("정밀 분석", targets, (pairs, prog, ct) => remote.AnalyzeManyAsync(pairs, prog, ct));
        else
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
                ? (Backend!.CreateMissingRepos ? $"{to.Label()}에 새로 생성: {create}개" : $"{to.Label()}에 없는 {create}개는 자동 생성이 꺼져 있어 건너뜀")
                : $"{to.Label()}에 없는 저장소 없음",
            "새 브랜치 생성 + fast-forward 가능한 브랜치 업데이트",
            Backend!.SyncTags ? "누락된 태그 푸시" : "태그는 동기화하지 않음",
            "분기된 브랜치는 덮어쓰지 않고 건너뜀",
        };
        if (!Dialogs.Confirm($"{from.Label()} → {to.Label()} 일괄 동기화",
                $"현재 목록의 저장소를 {to.Label()}로 동기화합니다.", "동기화 시작", false, details))
            return;

        var results = new List<SyncResult>();
        if (Backend.IsRemote)
            await RunRemoteBulkAsync($"{to.Label()}로 동기화", targets,
                async (pairs, prog, ct) => results.AddRange(await Backend.SyncManyAsync(pairs, from, prog, ct)));
        else
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
            await RunBatchAsync(items, Backend?.MaxParallel ?? 3, label, work, _bulkCts.Token);
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

    /// <summary>Bulk work done as one server job; per-repo views are refreshed afterwards.</summary>
    private async Task RunRemoteBulkAsync(string label, IReadOnlyList<RepoPairViewModel> items,
        Func<IReadOnlyList<RepoPair>, IProgress<(string Message, double Percent)>, CancellationToken, Task> work)
    {
        IsBusy = true;
        HasProgress = true;
        Progress = 0;
        BusyMessage = $"{label} (서버)";
        _bulkCts = new CancellationTokenSource();
        foreach (var i in items) i.SetWorking(true, "서버에서 처리 중…");
        try
        {
            await work(items.Select(i => i.Model).ToList(),
                new Progress<(string Message, double Percent)>(p => { BusyMessage = $"서버 · {p.Message}"; Progress = p.Percent; }),
                _bulkCts.Token);
        }
        catch (OperationCanceledException) { ShowToast("작업이 취소되었습니다.", "Warning"); }
        catch (Exception ex) { Logger.Error(ex.Message, "bulk", (ex as SyncException)?.Detail); ShowToast(ex.Message, "Error"); }
        finally
        {
            foreach (var i in items) { i.SetWorking(false, ""); i.Refresh(); }
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
        ApplySettings(refresh: false);
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
