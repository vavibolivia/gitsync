using System.Diagnostics;
using System.IO;
using GitSync.Core;
using GitSync.Desktop.Infrastructure;

namespace GitSync.Desktop.ViewModels;

/// <summary>
/// Settings page. In "Local" mode the GitHub/GitLab fields are this PC's engine options.
/// In "Server" mode the same fields edit the GitSync server's settings (tokens are write-only there).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _shell;
    private SyncOptions _localOptions;          // persisted local engine options (kept intact while in server mode)
    private bool _loading;

    public SettingsViewModel(MainViewModel shell)
    {
        _shell = shell;
        var s = SettingsStore.Load();
        _localOptions = s.Options;
        _mode = s.Mode;
        _showAdvanced = s.IsServer;
        _autoUpdate = s.AutoUpdate;
        _showAdvanced = s.IsServer;
        _serverUrl = s.ServerUrl;
        _serverKey = s.ServerKey;
        LoadFields(_localOptions);
        if (IsServerMode) ClearTokenFields();

        SaveCommand = new AsyncRelayCommand(SaveAsync);
        TestGitHubCommand = new AsyncRelayCommand(() => TestAsync(Provider.GitHub), () => IsServerMode || GitHubToken.Length > 0);
        TestGitLabCommand = new AsyncRelayCommand(() => TestAsync(Provider.GitLab), () => IsServerMode || GitLabToken.Length > 0);
        TestServerCommand = new AsyncRelayCommand(TestServerAsync, () => ServerUrl.Trim().Length > 0);
        OpenCacheCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(CacheRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CacheRoot}\"") { UseShellExecute = true });
        });
        ResetCacheCommand = new RelayCommand(() => CacheRoot = new SyncOptions().CacheRoot);
        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync, () => !UpdateBusy);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => !UpdateBusy && _pendingUpdate is not null);
        OpenReleasesCommand = new RelayCommand(() =>
            Process.Start(new ProcessStartInfo(AutoUpdater.ReleasesPageUrl) { UseShellExecute = true }));
    }

    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand TestGitHubCommand { get; }
    public AsyncRelayCommand TestGitLabCommand { get; }
    public AsyncRelayCommand TestServerCommand { get; }
    public RelayCommand OpenCacheCommand { get; }
    public AsyncRelayCommand CheckUpdateCommand { get; }
    public AsyncRelayCommand InstallUpdateCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }

    // ───────────────────────── software update ─────────────────────────

    private GitSync.Core.UpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private string _updateStatus = "";
    private bool? _updateOk;

    public string CurrentVersionText => $"v{AutoUpdater.CurrentVersion}";
    public bool UpdateBusy { get => _updateBusy; private set { if (Set(ref _updateBusy, value)) { System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } } }
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public bool? UpdateOk { get => _updateOk; private set => Set(ref _updateOk, value); }
    public bool UpdateAvailable => _pendingUpdate is not null;

    private async Task CheckUpdateAsync()
    {
        UpdateBusy = true;
        UpdateOk = null;
        UpdateStatus = "GitHub에서 최신 버전 확인 중…";
        try
        {
            var info = await AutoUpdater.CheckAsync();
            _pendingUpdate = info;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(UpdateAvailable));
            UpdateOk = true;
            UpdateStatus = info is null
                ? $"최신 버전입니다 ({CurrentVersionText})."
                : (AutoUpdater.CanSelfUpdate
                    ? $"새 버전 {info.Tag}이(가) 있습니다. '지금 설치'를 누르면 받아서 다시 시작합니다."
                    : $"새 버전 {info.Tag}이(가) 있습니다. 릴리스 페이지에서 받으세요.");
        }
        catch (Exception ex)
        {
            UpdateOk = false;
            UpdateStatus = $"업데이트 확인 실패: {ex.Message}. 네트워크가 GitHub(HTTPS)를 막고 있으면 '릴리스 페이지 열기'로 직접 받으세요.";
        }
        finally { UpdateBusy = false; }
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } info) return;
        UpdateBusy = true;
        UpdateOk = null;
        UpdateStatus = $"{info.Tag} 내려받는 중…";
        try
        {
            var file = await AutoUpdater.DownloadAsync(info);
            UpdateStatus = $"{info.Tag} 설치 후 다시 시작합니다…";
            await Task.Delay(800);
            SettingsStore.Backup();
            AutoUpdater.ApplyAndRestart(file, info);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateOk = false;
            UpdateStatus = $"설치 실패: {ex.Message}. '릴리스 페이지 열기'로 직접 받아 교체하세요.";
        }
        finally { UpdateBusy = false; }
    }
    public RelayCommand ResetCacheCommand { get; }

    public IReadOnlyList<string> VisibilityOptions { get; } = ["private", "internal", "public", "source"];

    // ───────────────────────── connection mode ─────────────────────────

    private string _mode, _serverUrl, _serverKey;
    private string? _serverTestResult;
    private bool? _serverTestOk;
    private bool _serverHasGitHubToken, _serverHasGitLabToken;

    public string Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            var wasServer = IsServerMode;
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsServerMode));
            OnPropertyChanged(nameof(IsLocalMode));
            OnPropertyChanged(nameof(TokenHint));
            IsDirty = true;
            GitHubTestResult = GitLabTestResult = null;
            if (IsServerMode && !wasServer) { _localOptions = ToLocalOptions(); ClearTokenFields(); }
            else if (!IsServerMode && wasServer) LoadFields(_localOptions);
        }
    }

    public bool IsServerMode => _mode == "Server";
    public bool IsLocalMode => !IsServerMode;
    public string ServerUrl { get => _serverUrl; set => Dirty(ref _serverUrl, value); }
    public string ServerKey { get => _serverKey; set => Dirty(ref _serverKey, value); }
    public string? ServerTestResult { get => _serverTestResult; private set => Set(ref _serverTestResult, value); }
    public bool? ServerTestOk { get => _serverTestOk; private set => Set(ref _serverTestOk, value); }

    public string TokenHint => IsServerMode ? "비워두면 서버에 저장된 키를 그대로 사용합니다." : "";
    public string GitHubTokenState => IsServerMode ? (_serverHasGitHubToken ? "서버에 저장됨" : "서버에 없음") : "";
    public string GitLabTokenState => IsServerMode ? (_serverHasGitLabToken ? "서버에 저장됨" : "서버에 없음") : "";

    // ───────────────────────── engine fields ─────────────────────────

    private string _gitHubApiUrl = "", _gitHubToken = "", _gitHubOwner = "";
    private string _gitLabUrl = "", _gitLabToken = "", _gitLabGroup = "", _gitLabTargetNamespace = "", _gitLabVisibility = "private";
    private string _cacheRoot = "", _nameMappings = "", _gitLabUser = "", _pairs = "";
    private bool _showAdvanced;
    private bool _autoUpdate = true;
    private bool _syncTags, _createMissingRepos, _includeArchived;
    private int _maxParallel, _autoSyncMinutes;
    private string? _gitHubTestResult, _gitLabTestResult;
    private bool? _gitHubTestOk, _gitLabTestOk;
    private bool _isDirty;

    public string GitHubApiUrl { get => _gitHubApiUrl; set => Dirty(ref _gitHubApiUrl, value); }
    public string GitHubToken { get => _gitHubToken; set => Dirty(ref _gitHubToken, value); }
    public string GitHubOwner { get => _gitHubOwner; set => Dirty(ref _gitHubOwner, value); }
    public string GitLabUser { get => _gitLabUser; set => Dirty(ref _gitLabUser, value); }
    /// <summary>Shows the rarely needed fields (server addresses, run location, sync options).</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Dirty(ref _autoUpdate, value); }
    public bool ShowAdvanced { get => _showAdvanced; set => Set(ref _showAdvanced, value); }
    /// <summary>Pairs added by hand, one "githubRepo=gitlabProject" per line (local mode).</summary>
    public string Pairs => _pairs;
    public string GitLabUrl { get => _gitLabUrl; set => Dirty(ref _gitLabUrl, value); }
    public string GitLabToken { get => _gitLabToken; set => Dirty(ref _gitLabToken, value); }
    public string GitLabGroup { get => _gitLabGroup; set { Dirty(ref _gitLabGroup, value); OnPropertyChanged(nameof(EffectiveNamespace)); } }
    public string GitLabTargetNamespace { get => _gitLabTargetNamespace; set { Dirty(ref _gitLabTargetNamespace, value); OnPropertyChanged(nameof(EffectiveNamespace)); } }
    public string GitLabVisibility { get => _gitLabVisibility; set => Dirty(ref _gitLabVisibility, value); }
    public string CacheRoot { get => _cacheRoot; set => Dirty(ref _cacheRoot, value); }
    public string NameMappings { get => _nameMappings; set => Dirty(ref _nameMappings, value); }
    public bool SyncTags { get => _syncTags; set => Dirty(ref _syncTags, value); }
    public bool CreateMissingRepos { get => _createMissingRepos; set => Dirty(ref _createMissingRepos, value); }
    public bool IncludeArchived { get => _includeArchived; set => Dirty(ref _includeArchived, value); }
    public int MaxParallel { get => _maxParallel; set => Dirty(ref _maxParallel, Math.Clamp(value, 1, 8)); }
    public int AutoSyncMinutes { get => _autoSyncMinutes; set => Dirty(ref _autoSyncMinutes, Math.Max(0, value)); }

    public string? GitHubTestResult { get => _gitHubTestResult; private set => Set(ref _gitHubTestResult, value); }
    public string? GitLabTestResult { get => _gitLabTestResult; private set => Set(ref _gitLabTestResult, value); }
    public bool? GitHubTestOk { get => _gitHubTestOk; private set => Set(ref _gitHubTestOk, value); }
    public bool? GitLabTestOk { get => _gitLabTestOk; private set => Set(ref _gitLabTestOk, value); }
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

    /// <summary>Local: both tokens present. Server: a server address is set.</summary>
    public bool IsConfigured => IsServerMode
        ? ServerUrl.Trim().Length > 0
        : _localOptions.IsGitHubConfigured && _localOptions.IsGitLabConfigured;

    public string EffectiveNamespace => GitLabTargetNamespace.Trim().Length > 0 ? GitLabTargetNamespace.Trim() : GitLabGroup.Trim();

    private void Dirty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name) && !_loading) IsDirty = true;
    }

    private void LoadFields(SyncOptions o)
    {
        _loading = true;
        GitHubApiUrl = o.GitHubApiUrl; GitHubToken = o.GitHubToken; GitHubOwner = o.GitHubOwner;
        GitLabUrl = o.GitLabUrl; GitLabToken = o.GitLabToken; GitLabGroup = o.GitLabGroup; GitLabUser = o.GitLabUser; _pairs = o.Pairs ?? "";
        GitLabTargetNamespace = o.GitLabTargetNamespace; GitLabVisibility = o.GitLabVisibility;
        CacheRoot = o.CacheRoot; NameMappings = o.NameMappings;
        SyncTags = o.SyncTags; CreateMissingRepos = o.CreateMissingRepos; IncludeArchived = o.IncludeArchived;
        MaxParallel = Math.Clamp(o.MaxParallel, 1, 8);
        _loading = false;
    }

    private void ClearTokenFields()
    {
        _loading = true;
        GitHubToken = "";
        GitLabToken = "";
        _loading = false;
    }

    /// <summary>Replaces the hand-made pair list and persists it right away (other unsaved edits are not written).</summary>
    public void SavePairs(string pairs)
    {
        _pairs = pairs;
        _localOptions.Pairs = pairs;
        _localOptions.ManualPairs = true;
        var stored = SettingsStore.Load();
        stored.Options.Pairs = pairs;
        stored.Options.ManualPairs = true;
        SettingsStore.Save(stored);
    }

    /// <summary>Engine options for running locally.</summary>
    public SyncOptions ToLocalOptions() => IsServerMode ? _localOptions.Clone() : new SyncOptions
    {
        GitHubApiUrl = GitHubApiUrl.Trim(), GitHubToken = GitHubToken.Trim(), GitHubOwner = GitHubOwner.Trim(),
        GitLabUrl = GitLabUrl.Trim(), GitLabToken = GitLabToken.Trim(), GitLabGroup = GitLabGroup.Trim(),
        GitLabUser = GitLabUser.Trim(), ManualPairs = true, Pairs = _pairs,
        GitLabTargetNamespace = GitLabTargetNamespace.Trim(), GitLabVisibility = GitLabVisibility,
        CacheRoot = CacheRoot.Trim(), NameMappings = NameMappings,
        SyncTags = SyncTags, CreateMissingRepos = CreateMissingRepos, IncludeArchived = IncludeArchived,
        MaxParallel = MaxParallel,
    };

    public DesktopSettings ToDesktopSettings() => new()
    {
        Mode = _mode, ServerUrl = ServerUrl.Trim(), ServerKey = ServerKey.Trim(), Options = ToLocalOptions(), AutoUpdate = AutoUpdate,
    };

    /// <summary>Server settings payload (empty tokens keep the server's tokens).</summary>
    private object ToServerInput() => new
    {
        gitHubApiUrl = GitHubApiUrl.Trim(), gitHubOwner = GitHubOwner.Trim(),
        gitHubToken = GitHubToken.Trim().Length > 0 ? GitHubToken.Trim() : null,
        gitLabUrl = GitLabUrl.Trim(), gitLabGroup = GitLabGroup.Trim(), gitLabTargetNamespace = GitLabTargetNamespace.Trim(),
        gitLabVisibility = GitLabVisibility,
        gitLabToken = GitLabToken.Trim().Length > 0 ? GitLabToken.Trim() : null,
        syncTags = SyncTags, createMissingRepos = CreateMissingRepos, includeArchived = IncludeArchived,
        maxParallel = MaxParallel, nameMappings = NameMappings, autoSyncMinutes = AutoSyncMinutes,
    };

    /// <summary>Fills the fields from the server (server mode).</summary>
    public async Task LoadFromServerAsync(RemoteSyncClient client)
    {
        var r = await client.GetSettingsAsync();
        _loading = true;
        GitHubApiUrl = r.GitHubApiUrl ?? ""; GitHubOwner = r.GitHubOwner ?? "";
        GitLabUrl = r.GitLabUrl ?? ""; GitLabGroup = r.GitLabGroup ?? ""; GitLabTargetNamespace = r.GitLabTargetNamespace ?? "";
        GitLabVisibility = r.GitLabVisibility ?? "private";
        SyncTags = r.SyncTags; CreateMissingRepos = r.CreateMissingRepos; IncludeArchived = r.IncludeArchived;
        MaxParallel = Math.Clamp(r.MaxParallel, 1, 8); NameMappings = r.NameMappings ?? ""; AutoSyncMinutes = r.AutoSyncMinutes;
        GitHubToken = ""; GitLabToken = "";
        _loading = false;
        _serverHasGitHubToken = r.HasGitHubToken;
        _serverHasGitLabToken = r.HasGitLabToken;
        OnPropertyChanged(nameof(GitHubTokenState));
        OnPropertyChanged(nameof(GitLabTokenState));
    }

    // ───────────────────────── actions ─────────────────────────

    private async Task SaveAsync()
    {
        var desktop = ToDesktopSettings();
        try
        {
            if (desktop.IsServer)
            {
                using var client = new RemoteSyncClient(desktop.ServerUrl, desktop.ServerKey, _shell.Logger);
                await client.SaveSettingsAsync(ToServerInput());
                await LoadFromServerAsync(client);
            }
            SettingsStore.Save(desktop);
            if (!desktop.IsServer) _localOptions = desktop.Options;
        }
        catch (Exception ex)
        {
            _shell.ShowToast($"설정 저장 실패: {ex.Message}", "Error");
            return;
        }
        IsDirty = false;
        OnPropertyChanged(nameof(IsConfigured));
        _shell.ShowToast(desktop.IsServer ? $"서버({desktop.ServerUrl}) 설정을 저장했습니다." : "설정을 저장했습니다.", "Success");
        _shell.Logger.Info("설정 저장", "settings");
        _shell.ApplySettings(refresh: IsConfigured);
        if (IsConfigured) _shell.CurrentPage = "Repos";
    }

    private async Task TestServerAsync()
    {
        ServerTestOk = null;
        ServerTestResult = "확인 중…";
        try
        {
            using var client = new RemoteSyncClient(ServerUrl, ServerKey, _shell.Logger);
            var status = await client.GetStatusAsync();
            await LoadFromServerAsync(client);
            ServerTestOk = true;
            ServerTestResult = status.Configured
                ? "연결됨 · 서버 설정을 불러왔습니다"
                : "연결됨 · 서버에 아직 키가 없습니다. 위에 입력 후 저장하세요";
        }
        catch (Exception ex)
        {
            ServerTestOk = false;
            ServerTestResult = ex.Message;
        }
    }

    private async Task TestAsync(Provider provider)
    {
        void Report(bool? ok, string text)
        {
            if (provider == Provider.GitHub) { GitHubTestOk = ok; GitHubTestResult = text; }
            else { GitLabTestOk = ok; GitLabTestResult = text; }
        }
        Report(null, "확인 중…");
        try
        {
            string user;
            if (IsServerMode)
            {
                using var client = new RemoteSyncClient(ServerUrl, ServerKey, _shell.Logger);
                user = await client.TestAsync(provider == Provider.GitHub ? "github" : "gitlab", ToServerInput());
            }
            else
            {
                using var svc = new SyncService(ToLocalOptions(), _shell.Logger);
                user = provider == Provider.GitHub ? await svc.TestGitHubAsync() : await svc.TestGitLabAsync();
            }
            Report(true, $"연결됨 · {user}" + (IsServerMode ? " (서버에서 확인)" : ""));
        }
        catch (Exception ex)
        {
            Report(false, ex.Message);
        }
    }
}
