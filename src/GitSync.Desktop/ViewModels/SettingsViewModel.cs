using System.Diagnostics;
using System.IO;
using GitSync.Core;
using GitSync.Desktop.Infrastructure;

namespace GitSync.Desktop.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    public SettingsViewModel(MainViewModel shell)
    {
        _shell = shell;
        Load(SettingsStore.Load());
        SaveCommand = new RelayCommand(Save);
        TestGitHubCommand = new AsyncRelayCommand(TestGitHubAsync, () => GitHubToken.Length > 0);
        TestGitLabCommand = new AsyncRelayCommand(TestGitLabAsync, () => GitLabToken.Length > 0);
        OpenCacheCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(CacheRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CacheRoot}\"") { UseShellExecute = true });
        });
        ResetCacheCommand = new RelayCommand(() => CacheRoot = new SyncOptions().CacheRoot);
    }

    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand TestGitHubCommand { get; }
    public AsyncRelayCommand TestGitLabCommand { get; }
    public RelayCommand OpenCacheCommand { get; }
    public RelayCommand ResetCacheCommand { get; }

    public IReadOnlyList<string> VisibilityOptions { get; } = ["private", "internal", "public", "source"];

    private string _gitHubApiUrl = "", _gitHubToken = "", _gitHubOwner = "";
    private string _gitLabUrl = "", _gitLabToken = "", _gitLabGroup = "", _gitLabTargetNamespace = "", _gitLabVisibility = "private";
    private string _cacheRoot = "", _nameMappings = "";
    private bool _syncTags, _createMissingRepos, _includeArchived;
    private int _maxParallel;
    private string? _gitHubTestResult, _gitLabTestResult;
    private bool? _gitHubTestOk, _gitLabTestOk;
    private bool _isDirty;

    public string GitHubApiUrl { get => _gitHubApiUrl; set => Dirty(ref _gitHubApiUrl, value); }
    public string GitHubToken { get => _gitHubToken; set => Dirty(ref _gitHubToken, value); }
    public string GitHubOwner { get => _gitHubOwner; set => Dirty(ref _gitHubOwner, value); }
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

    public string? GitHubTestResult { get => _gitHubTestResult; private set => Set(ref _gitHubTestResult, value); }
    public string? GitLabTestResult { get => _gitLabTestResult; private set => Set(ref _gitLabTestResult, value); }
    public bool? GitHubTestOk { get => _gitHubTestOk; private set => Set(ref _gitHubTestOk, value); }
    public bool? GitLabTestOk { get => _gitLabTestOk; private set => Set(ref _gitLabTestOk, value); }
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

    public bool IsConfigured => GitHubToken.Trim().Length > 0 && GitLabToken.Trim().Length > 0;
    public string EffectiveNamespace => GitLabTargetNamespace.Trim().Length > 0 ? GitLabTargetNamespace.Trim() : GitLabGroup.Trim();

    private void Dirty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) IsDirty = true;
    }

    private void Load(SyncOptions o)
    {
        _gitHubApiUrl = o.GitHubApiUrl; _gitHubToken = o.GitHubToken; _gitHubOwner = o.GitHubOwner;
        _gitLabUrl = o.GitLabUrl; _gitLabToken = o.GitLabToken; _gitLabGroup = o.GitLabGroup;
        _gitLabTargetNamespace = o.GitLabTargetNamespace; _gitLabVisibility = o.GitLabVisibility;
        _cacheRoot = o.CacheRoot; _nameMappings = o.NameMappings;
        _syncTags = o.SyncTags; _createMissingRepos = o.CreateMissingRepos; _includeArchived = o.IncludeArchived;
        _maxParallel = Math.Clamp(o.MaxParallel, 1, 8);
    }

    public SyncOptions ToOptions() => new()
    {
        GitHubApiUrl = GitHubApiUrl.Trim(), GitHubToken = GitHubToken.Trim(), GitHubOwner = GitHubOwner.Trim(),
        GitLabUrl = GitLabUrl.Trim(), GitLabToken = GitLabToken.Trim(), GitLabGroup = GitLabGroup.Trim(),
        GitLabTargetNamespace = GitLabTargetNamespace.Trim(), GitLabVisibility = GitLabVisibility,
        CacheRoot = CacheRoot.Trim(), NameMappings = NameMappings,
        SyncTags = SyncTags, CreateMissingRepos = CreateMissingRepos, IncludeArchived = IncludeArchived,
        MaxParallel = MaxParallel,
    };

    private void Save()
    {
        var o = ToOptions();
        try
        {
            SettingsStore.Save(o);
        }
        catch (Exception ex)
        {
            _shell.ShowToast($"설정 저장 실패: {ex.Message}", "Error");
            return;
        }
        IsDirty = false;
        OnPropertyChanged(nameof(IsConfigured));
        _shell.ShowToast("설정을 저장했습니다.", "Success");
        _shell.Logger.Info("설정 저장", "settings");
        _shell.ApplySettings(o, refresh: IsConfigured);
        if (IsConfigured) _shell.CurrentPage = "Repos";
    }

    private async Task TestGitHubAsync()
    {
        GitHubTestOk = null;
        GitHubTestResult = "확인 중…";
        using var svc = new SyncService(ToOptions(), _shell.Logger);
        try
        {
            var login = await svc.TestGitHubAsync();
            GitHubTestOk = true;
            GitHubTestResult = $"연결됨 · {login}";
        }
        catch (Exception ex)
        {
            GitHubTestOk = false;
            GitHubTestResult = ex.Message;
        }
    }

    private async Task TestGitLabAsync()
    {
        GitLabTestOk = null;
        GitLabTestResult = "확인 중…";
        using var svc = new SyncService(ToOptions(), _shell.Logger);
        try
        {
            var user = await svc.TestGitLabAsync();
            GitLabTestOk = true;
            GitLabTestResult = $"연결됨 · {user}";
        }
        catch (Exception ex)
        {
            GitLabTestOk = false;
            GitLabTestResult = ex.Message;
        }
    }
}
