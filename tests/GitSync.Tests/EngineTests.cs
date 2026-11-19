using GitSync.Core;

namespace GitSync.Tests;

/// <summary>End-to-end tests of the sync engine against <see cref="FakeForge"/> (real git over HTTP).</summary>
public sealed class EngineTests : IAsyncLifetime
{
    private readonly FakeForge _forge = new();
    private readonly SyncLogger _log = new();
    private readonly List<LogEntry> _entries = new();
    private string _work = "";
    private string _cache = "";

    public async Task InitializeAsync()
    {
        _log.Logged += e => { lock (_entries) _entries.Add(e); };
        await _forge.StartAsync();
        _work = Path.Combine(_forge.Root, "work");
        _cache = Path.Combine(_forge.Root, "cache");
        await FakeForge.Git(null, "init", "-q", "-b", "main", _work);
        await FakeForge.Git(_work, "config", "user.email", "dev@example.com");
        await FakeForge.Git(_work, "config", "user.name", "Dev");
        await FakeForge.Git(_work, "config", "core.quotepath", "false");
    }

    public async Task DisposeAsync() => await _forge.DisposeAsync();

    private SyncOptions Options(Action<SyncOptions>? tweak = null)
    {
        var o = new SyncOptions
        {
            GitHubApiUrl = _forge.GitHubApiUrl, GitHubToken = FakeForge.GitHubToken,
            GitLabUrl = _forge.GitLabUrl, GitLabToken = FakeForge.GitLabToken,
            GitLabGroup = "mirrors", CacheRoot = _cache, MaxParallel = 3,
        };
        tweak?.Invoke(o);
        return o;
    }

    private async Task Commit(string msg)
    {
        await File.AppendAllTextAsync(Path.Combine(_work, "file.txt"), msg + "\n");
        await FakeForge.Git(_work, "add", "-A");
        await FakeForge.Git(_work, "commit", "-q", "-m", msg);
    }

    private Task Push(string bare, params string[] refspecs) => FakeForge.Git(_work, ["push", "-q", "--force", bare, .. refspecs]);

    /// <summary>
    /// octo/app on GitHub and mirrors/app on GitLab with:
    ///   main       GitHub ahead by 2
    ///   dev        diverged (1 / 1)
    ///   feature/한글-x   only on GitHub
    ///   gl-only    only on GitLab
    ///   tag v1     only on GitHub
    /// </summary>
    private async Task SeedScenarioAsync()
    {
        await _forge.AddGitHubAsync("octo/app");
        await _forge.AddGitLabAsync("mirrors/app");
        var gh = _forge.GitHubPath("octo/app");
        var gl = _forge.GitLabPath("mirrors/app");

        await Commit("c1");
        await Push(gl, "main");
        await FakeForge.Git(_work, "tag", "-a", "v1", "-m", "v1");
        await Commit("c2");
        await Commit("c3");
        await Push(gh, "main", "v1");

        await FakeForge.Git(_work, "checkout", "-q", "-b", "feature/한글-x");
        await Commit("feature");
        await Push(gh, "feature/한글-x");

        await FakeForge.Git(_work, "checkout", "-q", "main");
        await FakeForge.Git(_work, "checkout", "-q", "-b", "dev");
        await Commit("dev-common");
        await Push(gh, "dev");
        await Push(gl, "dev");
        await Commit("dev-gh");
        await Push(gh, "dev");
        await FakeForge.Git(_work, "reset", "-q", "--hard", "HEAD~1");
        await Commit("dev-gl");
        await Push(gl, "dev");

        await FakeForge.Git(_work, "checkout", "-q", "-b", "gl-only");
        await Push(gl, "gl-only");
        await FakeForge.Git(_work, "checkout", "-q", "main");
    }

    private static BranchState B(RepoPair p, string name) => p.Branches.Single(b => b.Name == name);

    // ───────────────────────── connection ─────────────────────────

    [Fact]
    public async Task Connection_test_returns_account_names()
    {
        using var svc = new SyncService(Options(), _log);
        Assert.Equal(FakeForge.GitHubLogin, await svc.TestGitHubAsync());
        Assert.Equal(FakeForge.GitLabUser, await svc.TestGitLabAsync());
    }

    [Fact]
    public async Task Wrong_tokens_give_clear_errors()
    {
        using var svc = new SyncService(Options(o => { o.GitHubToken = "bad"; o.GitLabToken = "bad"; }), _log);
        var e1 = await Assert.ThrowsAsync<SyncException>(() => svc.TestGitHubAsync());
        Assert.Contains("401", e1.Message);
        Assert.Contains("토큰", e1.Message);
        var e2 = await Assert.ThrowsAsync<SyncException>(() => svc.TestGitLabAsync());
        Assert.Contains("401", e2.Message);
    }

    [Fact]
    public async Task Unreachable_server_gives_clear_error()
    {
        using var svc = new SyncService(Options(o => o.GitHubApiUrl = "http://127.0.0.1:1"), _log);
        var e = await Assert.ThrowsAsync<SyncException>(() => svc.TestGitHubAsync());
        Assert.Contains("연결할 수 없습니다", e.Message);
    }

    // ───────────────────────── scan / matching ─────────────────────────

    [Fact]
    public async Task Scan_finds_all_repositories_across_pages_and_matches_by_name()
    {
        for (int i = 0; i < 105; i++) await _forge.AddGitHubAsync($"octo/repo-{i:000}", materialize: false);
        await _forge.AddGitHubAsync("octo/archived-one", materialize: false, archived: true);
        for (int i = 0; i < 103; i += 2) await _forge.AddGitLabAsync($"mirrors/repo-{i:000}", materialize: false);
        await _forge.AddGitLabAsync("mirrors/sub/repo-001", materialize: false);    // subgroup project
        await _forge.AddGitLabAsync("other/repo-003", materialize: false);          // outside group → not scanned
        await _forge.AddGitLabAsync("mirrors/only-on-gitlab", materialize: false);

        using var svc = new SyncService(Options(), _log);
        var pairs = await svc.ScanAsync();

        Assert.Equal(105, pairs.Count(p => p.GitHub is not null));               // pagination (100 + 5), archived excluded
        Assert.DoesNotContain(pairs, p => p.Name == "archived-one");
        Assert.True(pairs.Single(p => p.Name == "repo-000").HasBoth);
        Assert.Equal("mirrors/repo-000", pairs.Single(p => p.Name == "repo-000").GitLab!.FullName);
        Assert.Equal("mirrors/sub/repo-001", pairs.Single(p => p.Name == "repo-001").GitLab!.FullName); // subgroup included
        Assert.Null(pairs.Single(p => p.Name == "repo-003").GitLab);                                    // "other" group not scanned
        Assert.Equal(RepoStatus.GitHubOnly, pairs.Single(p => p.Name == "repo-003").Status);
        Assert.Equal(RepoStatus.GitLabOnly, pairs.Single(p => p.Name == "only-on-gitlab" && p.GitHub is null).Status);
        Assert.Contains(_forge.RequestLog, r => r.Contains("/gh/api/user/repos") && r.Contains("page=2"));
        Assert.Contains(_forge.RequestLog, r => r.Contains("/gl/api/v4/groups/mirrors/projects"));
    }

    [Fact]
    public async Task Scan_includes_archived_when_enabled_and_supports_org_owner()
    {
        await _forge.AddGitHubAsync("acme/svc", materialize: false);
        await _forge.AddGitHubAsync("acme/old", materialize: false, archived: true);
        await _forge.AddGitHubAsync("octo/mine", materialize: false);
        using var svc = new SyncService(Options(o => { o.GitHubOwner = "acme"; o.IncludeArchived = true; }), _log);
        var pairs = await svc.ScanAsync();
        Assert.Equal(new[] { "old", "svc" }, pairs.Where(p => p.GitHub is not null).Select(p => p.Name).OrderBy(n => n));
        Assert.Contains(_forge.RequestLog, r => r.Contains("/gh/api/orgs/acme/repos"));
    }

    [Fact]
    public async Task Name_mappings_override_matching_including_full_paths()
    {
        await _forge.AddGitHubAsync("octo/frontend", materialize: false);
        await _forge.AddGitHubAsync("octo/backend", materialize: false);
        await _forge.AddGitLabAsync("mirrors/web-ui", materialize: false);
        await _forge.AddGitLabAsync("mirrors/sub/api", materialize: false);
        using var svc = new SyncService(Options(o => o.NameMappings = "# comment\nfrontend = web-ui\nbackend=mirrors/sub/api\n"), _log);
        var pairs = await svc.ScanAsync();
        Assert.Equal("mirrors/web-ui", pairs.Single(p => p.Name == "frontend").GitLab!.FullName);
        Assert.Equal("mirrors/sub/api", pairs.Single(p => p.Name == "backend").GitLab!.FullName);
        Assert.Equal(2, pairs.Count);
    }

    [Fact]
    public async Task GitHub_id_that_is_an_email_falls_back_to_the_token_account()
    {
        // The GitHub "ID" field takes a username; an email (or any non-user) must not 404 the whole scan.
        await _forge.AddGitHubAsync("octo/mine", materialize: false);
        await _forge.AddGitLabAsync("mirrors/mine", materialize: false);
        using var svc = new SyncService(Options(o => o.GitHubOwner = "vavibolivia@gmail.com"), _log);
        var pairs = await svc.ScanAsync();
        Assert.Contains(pairs, p => p.Name == "mine" && p.GitHub?.FullName == "octo/mine");
        Assert.Contains(svc.LastGitHubRepos, r => r.FullName == "octo/mine");
    }

    [Fact]
    public async Task Manual_pairs_show_only_the_repositories_the_user_added()
    {
        await _forge.AddGitHubAsync("octo/frontend", materialize: false);
        await _forge.AddGitHubAsync("octo/backend", materialize: false);
        await _forge.AddGitHubAsync("octo/solo", materialize: false);
        await _forge.AddGitHubAsync("octo/ignored", materialize: false);
        await _forge.AddGitLabAsync("mirrors/web-ui", materialize: false);
        await _forge.AddGitLabAsync("mirrors/backend", materialize: false);
        await _forge.AddGitLabAsync("mirrors/gl-only", materialize: false);
        await _forge.AddGitLabAsync("mirrors/ignored", materialize: false);

        using var svc = new SyncService(Options(o =>
        {
            o.ManualPairs = true;
            o.Pairs = "octo/frontend=mirrors/web-ui\nbackend=\nsolo=\n=mirrors/gl-only\nmissing=nowhere\n";
        }), _log);
        var pairs = await svc.ScanAsync();

        Assert.Equal(new[] { "backend", "frontend", "gl-only", "solo" }, pairs.Select(p => p.Name));
        Assert.Equal("mirrors/web-ui", pairs.Single(p => p.Name == "frontend").GitLab!.FullName);   // explicit, different names
        Assert.Equal("mirrors/backend", pairs.Single(p => p.Name == "backend").GitLab!.FullName);   // empty side = same name
        Assert.Equal(RepoStatus.GitHubOnly, pairs.Single(p => p.Name == "solo").Status);            // created on sync
        Assert.Equal(RepoStatus.GitLabOnly, pairs.Single(p => p.Name == "gl-only").Status);
        Assert.Contains(svc.LastGitHubRepos, r => r.Name == "ignored");                             // still offered for picking
        Assert.Contains(_log.Since(0), e => e.Level == SyncLogLevel.Warning && e.Message.Contains("missing"));
    }

    [Theory]
    [InlineData("", "https://api.github.com")]
    [InlineData("github.com", "https://api.github.com")]
    [InlineData("https://api.github.com/", "https://api.github.com")]
    [InlineData("10.0.0.5", "https://10.0.0.5/api/v3")]
    [InlineData("http://ghe.corp.local", "http://ghe.corp.local/api/v3")]
    [InlineData("https://ghe.corp.local/api/v3", "https://ghe.corp.local/api/v3")]
    public void GitHub_address_accepts_a_host_ip_or_url(string input, string expected) =>
        Assert.Equal(expected, SyncOptions.NormalizeGitHubApiUrl(input));

    [Theory]
    [InlineData("v1.2.0", "1.1.0", true)]
    [InlineData("v1.1.0", "1.1.0", false)]
    [InlineData("v1.0.9", "1.1.0", false)]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("not-a-version", "1.0.0", false)]
    public void Update_is_offered_only_for_a_newer_release(string tag, string current, bool expected)
    {
        var json = $$"""
            {"tag_name":"{{tag}}","draft":false,"prerelease":false,"assets":[
              {"name":"GitSync-win-x64.zip","size":1,"browser_download_url":"https://example.com/z.zip"},
              {"name":"GitSync.exe","size":123,"digest":"sha256:ABCDEF","browser_download_url":"https://example.com/GitSync.exe"}]}
            """;
        var info = UpdateChecker.ParseLatest(json, Version.Parse(current));
        Assert.Equal(expected, info is not null);
        if (info is null) return;
        Assert.Equal("https://example.com/GitSync.exe", info.DownloadUrl);
        Assert.Equal(123, info.Size);
        Assert.Equal("ABCDEF", info.Sha256);
    }

    [Fact]
    public void Update_captures_the_zip_asset_as_a_fallback()
    {
        var json = """
            {"tag_name":"v2.0.0","draft":false,"prerelease":false,"assets":[
              {"name":"GitSync-win-x64.zip","size":10,"browser_download_url":"https://example.com/GitSync-win-x64.zip"},
              {"name":"GitSync.exe","size":123,"digest":"sha256:ABC","browser_download_url":"https://example.com/GitSync.exe"}]}
            """;
        var info = UpdateChecker.ParseLatest(json, new Version(1, 0, 0));
        Assert.NotNull(info);
        Assert.Equal("https://example.com/GitSync.exe", info!.DownloadUrl);
        Assert.Equal("https://example.com/GitSync-win-x64.zip", info.ZipUrl);
    }

    [Fact]
    public void Update_ignores_prereleases_and_releases_without_the_exe()
    {
        var current = new Version(1, 0, 0);
        Assert.Null(UpdateChecker.ParseLatest("""{"tag_name":"v9.0.0","prerelease":true,"assets":[{"name":"GitSync.exe","browser_download_url":"https://example.com/a"}]}""", current));
        Assert.Null(UpdateChecker.ParseLatest("""{"tag_name":"v9.0.0","assets":[{"name":"other.zip","browser_download_url":"https://example.com/a"}]}""", current));
        Assert.Null(UpdateChecker.ParseLatest("""{"message":"Not Found"}""", current));
    }

    [Theory]
    [InlineData("", "https://gitlab.com")]
    [InlineData("gitlab.com", "https://gitlab.com")]
    [InlineData("10.0.0.7:8443", "https://10.0.0.7:8443")]
    [InlineData("http://gitlab.corp.local/", "http://gitlab.corp.local")]
    public void GitLab_address_accepts_a_host_ip_or_url(string input, string expected) =>
        Assert.Equal(expected, SyncOptions.NormalizeGitLabUrl(input));

    [Fact]
    public async Task GitLab_id_must_match_the_account_of_the_key()
    {
        using var ok = new SyncService(Options(o => o.GitLabUser = FakeForge.GitLabUser.ToUpperInvariant()), _log);
        Assert.Equal(FakeForge.GitLabUser, await ok.TestGitLabAsync());
        using var bad = new SyncService(Options(o => o.GitLabUser = "someone-else"), _log);
        var e = await Assert.ThrowsAsync<SyncException>(() => bad.TestGitLabAsync());
        Assert.Contains("someone-else", e.Message);
    }

    [Fact]
    public async Task Duplicate_names_prefer_target_namespace()
    {
        await _forge.AddGitHubAsync("octo/dup", materialize: false);
        await _forge.AddGitLabAsync("mirrors/sub/dup", materialize: false);
        await _forge.AddGitLabAsync("mirrors/dup", materialize: false);
        using var svc = new SyncService(Options(o => o.GitLabTargetNamespace = "mirrors"), _log);
        var pairs = await svc.ScanAsync();
        Assert.Equal("mirrors/dup", pairs.Single(p => p.GitHub is not null).GitLab!.FullName);
        Assert.Contains(_entries, e => e.Level == SyncLogLevel.Warning && e.Message.Contains("dup"));
    }

    // ───────────────────────── compare ─────────────────────────

    [Fact]
    public async Task Quick_compare_uses_api_head_shas()
    {
        await SeedScenarioAsync();
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "app");
        await svc.QuickCompareAsync(pair);

        Assert.False(pair.IsAnalyzed);
        Assert.Equal(BranchStatus.Differs, B(pair, "main").Status);
        Assert.Equal(BranchStatus.Differs, B(pair, "dev").Status);
        Assert.Equal(BranchStatus.GitHubOnly, B(pair, "feature/한글-x").Status);
        Assert.Equal(BranchStatus.GitLabOnly, B(pair, "gl-only").Status);
        Assert.Equal(RepoStatus.OutOfSync, pair.Status);
        Assert.True(B(pair, "main").IsDefault);
        Assert.Equal("main", pair.Branches[0].Name);
    }

    [Fact]
    public async Task Analyze_fetches_over_http_and_computes_ahead_behind()
    {
        await SeedScenarioAsync();
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "app");
        await svc.AnalyzeAsync(pair);

        Assert.True(pair.IsAnalyzed);
        var main = B(pair, "main");
        Assert.Equal(BranchStatus.GitHubAhead, main.Status);
        Assert.Equal(2, main.GitHubAhead);
        Assert.Equal(0, main.GitLabAhead);
        Assert.Equal("Dev", main.GitHubCommit!.Author);
        Assert.Equal("c3", main.GitHubCommit.Subject);
        Assert.NotNull(main.GitHubCommit.Date);

        var dev = B(pair, "dev");
        Assert.Equal(BranchStatus.Diverged, dev.Status);
        Assert.Equal((1, 1), (dev.GitHubAhead, dev.GitLabAhead));
        Assert.Equal(BranchStatus.GitHubOnly, B(pair, "feature/한글-x").Status);
        Assert.Equal(BranchStatus.GitLabOnly, B(pair, "gl-only").Status);
        Assert.Equal(new TagSummary(0, 1, 0, 0), pair.Tags);
        Assert.Empty(_forge.AuthFailures);
    }

    [Fact]
    public async Task Analyze_reports_gitlab_ahead()
    {
        await _forge.AddGitHubAsync("octo/x");
        await _forge.AddGitLabAsync("mirrors/x");
        await Commit("a");
        await Push(_forge.GitHubPath("octo/x"), "main");
        await Commit("b");
        await Push(_forge.GitLabPath("mirrors/x"), "main");
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "x");
        await svc.AnalyzeAsync(pair);
        Assert.Equal(BranchStatus.GitLabAhead, B(pair, "main").Status);
        Assert.Equal(1, B(pair, "main").GitLabAhead);
        Assert.False(SyncService.RequiresForce(B(pair, "main"), Provider.GitLab));
        Assert.True(SyncService.RequiresForce(B(pair, "main"), Provider.GitHub));
    }

    // ───────────────────────── sync ─────────────────────────

    /// <summary>Release after release: every version tag (and the commits it needs) must arrive on GitLab unchanged.</summary>
    [Fact]
    public async Task Version_tags_are_synced_release_by_release_and_moved_tags_are_not_overwritten()
    {
        await _forge.AddGitHubAsync("octo/app");
        await _forge.AddGitLabAsync("mirrors/app");
        var gh = _forge.GitHubPath("octo/app");
        var gl = _forge.GitLabPath("mirrors/app");
        using var svc = new SyncService(Options(o => { o.ManualPairs = true; o.Pairs = "octo/app=mirrors/app"; }), _log);

        async Task<RepoPair> SyncOnce()
        {
            var pair = (await svc.ScanAsync()).Single();
            await svc.QuickCompareAsync(pair);
            var r = await svc.SyncAsync(pair, Provider.GitHub);
            Assert.True(r.Ok, r.Error);
            return pair;
        }

        // v1.0.0
        await Commit("c1");
        await FakeForge.Git(_work, "tag", "-a", "v1.0.0", "-m", "release 1.0.0");
        await Push(gh, "main", "v1.0.0");
        await SyncOnce();

        // v1.0.1: a tag on the SAME commit (no branch change). The quick comparison must still report a difference.
        await FakeForge.Git(_work, "tag", "v1.0.1");
        await Push(gh, "v1.0.1");
        var quick = (await svc.ScanAsync()).Single();
        await svc.QuickCompareAsync(quick);
        Assert.Equal(new TagSummary(1, 1, 0, 0), quick.Tags);
        Assert.Equal(RepoStatus.OutOfSync, quick.Status);
        await SyncOnce();

        // v1.1.0 and v2.0.0: new commits, two releases between syncs, plus a release branch.
        await Commit("c2");
        await FakeForge.Git(_work, "tag", "-a", "v1.1.0", "-m", "release 1.1.0");
        await FakeForge.Git(_work, "checkout", "-q", "-b", "release/1.x");
        await FakeForge.Git(_work, "checkout", "-q", "main");
        await Commit("c3");
        await FakeForge.Git(_work, "tag", "-a", "v2.0.0", "-m", "release 2.0.0");
        await Push(gh, "main", "release/1.x", "v1.1.0", "v2.0.0");
        var pair = await SyncOnce();

        var a = await FakeForge.RefsAsync(gh);
        var b = await FakeForge.RefsAsync(gl);
        Assert.Equal(a.OrderBy(kv => kv.Key), b.OrderBy(kv => kv.Key));          // every branch and tag object identical
        Assert.Equal(6, a.Count);                                               // main, release/1.x, 4 version tags
        Assert.Equal(new TagSummary(4, 0, 0, 0), pair.Tags);
        Assert.Equal(RepoStatus.Synced, pair.Status);

        // Each version checks out to the same tree on GitLab as on GitHub.
        foreach (var v in new[] { "v1.0.0", "v1.0.1", "v1.1.0", "v2.0.0" })
            Assert.Equal(await FakeForge.Git(gh, "rev-parse", v + "^{tree}"), await FakeForge.Git(gl, "rev-parse", v + "^{tree}"));

        // Re-running changes nothing.
        var again = await svc.SyncAsync(pair, Provider.GitHub);
        Assert.Equal(0, again.Pushed);
        Assert.Equal(b.OrderBy(kv => kv.Key), (await FakeForge.RefsAsync(gl)).OrderBy(kv => kv.Key));

        // A version tag moved on GitHub after release is reported, never silently overwritten on GitLab.
        await FakeForge.Git(_work, "tag", "-f", "v1.0.0", "HEAD");
        await FakeForge.Git(_work, "push", "-q", "--force", gh, "refs/tags/v1.0.0");
        var moved = await SyncOnce();
        Assert.Equal(b["refs/tags/v1.0.0"], (await FakeForge.RefsAsync(gl))["refs/tags/v1.0.0"]);
        Assert.Equal(1, moved.Tags!.Different);
        Assert.Equal(RepoStatus.OutOfSync, moved.Status);
        Assert.Contains(_log.Since(0), e => e.Level == SyncLogLevel.Warning && e.Message.Contains("v1.0.0") && e.Message.Contains("덮어쓰지"));
    }

    [Fact]
    public async Task Sync_to_gitlab_creates_branches_fast_forwards_pushes_tags_and_skips_diverged()
    {
        await SeedScenarioAsync();
        var glBefore = await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/app"));
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "app");

        await svc.AnalyzeAsync(pair);
        var plan = SyncService.BuildPlan(pair, Provider.GitHub);
        Assert.Equal("main", plan.Actions[0].Branch);                                  // default branch first
        Assert.Contains(plan.Actions, a => a is { Branch: "main", Kind: BranchActionKind.FastForward, Commits: 2 });
        Assert.Contains(plan.Actions, a => a is { Branch: "feature/한글-x", Kind: BranchActionKind.Create });
        Assert.Contains(plan.Actions, a => a is { Branch: "dev", Kind: BranchActionKind.Skip });
        Assert.DoesNotContain(plan.Actions, a => a.Branch == "gl-only");

        var result = await svc.SyncAsync(pair, Provider.GitHub);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(1, result.Skipped);
        Assert.False(result.CreatedRepo);

        var gh = await FakeForge.RefsAsync(_forge.GitHubPath("octo/app"));
        var gl = await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/app"));
        Assert.Equal(gh["refs/heads/main"], gl["refs/heads/main"]);
        Assert.Equal(gh["refs/heads/feature/한글-x"], gl["refs/heads/feature/한글-x"]);
        Assert.Equal(gh["refs/tags/v1"], gl["refs/tags/v1"]);                         // annotated tag object identical
        Assert.Equal(glBefore["refs/heads/dev"], gl["refs/heads/dev"]);                 // diverged branch untouched
        Assert.Equal(glBefore["refs/heads/gl-only"], gl["refs/heads/gl-only"]);         // target-only branch untouched

        Assert.Equal(BranchStatus.Synced, B(pair, "main").Status);                       // state refreshed after sync
        Assert.Equal(BranchStatus.Diverged, B(pair, "dev").Status);
        Assert.Equal(new TagSummary(1, 0, 0, 0), pair.Tags);

        // Second run is a no-op.
        var again = await svc.SyncAsync(pair, Provider.GitHub);
        Assert.Equal(0, again.Pushed);
        Assert.Equal(gl, await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/app")));
    }

    [Fact]
    public async Task Force_push_overwrites_diverged_branch_only_when_requested()
    {
        await SeedScenarioAsync();
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "app");
        await svc.AnalyzeAsync(pair);
        Assert.True(SyncService.RequiresForce(B(pair, "dev"), Provider.GitHub));

        // Without force the server rejects the non-fast-forward push.
        var e = await Assert.ThrowsAsync<SyncException>(() => svc.PushBranchAsync(pair, "dev", Provider.GitHub, force: false));
        Assert.Contains("dev", e.Message);
        Assert.Contains("rejected", e.Detail ?? "", StringComparison.OrdinalIgnoreCase);

        await svc.PushBranchAsync(pair, "dev", Provider.GitHub, force: true);
        var gh = await FakeForge.RefsAsync(_forge.GitHubPath("octo/app"));
        var gl = await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/app"));
        Assert.Equal(gh["refs/heads/dev"], gl["refs/heads/dev"]);
        Assert.Equal(BranchStatus.Synced, B(pair, "dev").Status);
    }

    [Fact]
    public async Task Missing_gitlab_project_is_created_in_target_namespace_with_all_branches_and_tags()
    {
        await _forge.AddGitHubAsync("octo/newproj", isPrivate: false);
        var gh = _forge.GitHubPath("octo/newproj");
        await Commit("n1");
        await FakeForge.Git(_work, "tag", "v0.1");
        await FakeForge.Git(_work, "checkout", "-q", "-b", "release/1.0");
        await Commit("n2");
        await Push(gh, "main", "release/1.0", "v0.1");

        using var svc = new SyncService(Options(o => { o.GitLabTargetNamespace = "mirrors/sub"; o.GitLabVisibility = "source"; }), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "newproj");
        Assert.Equal(RepoStatus.GitHubOnly, pair.Status);

        var result = await svc.SyncAsync(pair, Provider.GitHub);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.CreatedRepo);
        Assert.Equal(2, result.Pushed);
        Assert.Equal("mirrors/sub/newproj", pair.GitLab!.FullName);
        Assert.Equal("public", _forge.CreatedVisibility["mirrors/sub/newproj"]);       // "source" visibility honoured

        var glPath = _forge.GitLabPath("mirrors/sub/newproj");
        var ghRefs = await FakeForge.RefsAsync(gh);
        var glRefs = await FakeForge.RefsAsync(glPath);
        Assert.Equal(ghRefs, glRefs);
        Assert.Equal("refs/heads/main", await FakeForge.Git(glPath, "symbolic-ref", "HEAD"));
        Assert.Contains(_forge.RequestLog, r => r.StartsWith("PUT /gl/api/v4/projects/"));   // default branch set
        Assert.Equal(RepoStatus.Synced, pair.Status);
    }

    [Fact]
    public async Task Missing_repo_is_not_created_when_disabled()
    {
        await _forge.AddGitHubAsync("octo/keep-out");
        using var svc = new SyncService(Options(o => o.CreateMissingRepos = false), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "keep-out");
        var result = await svc.SyncAsync(pair, Provider.GitHub);
        Assert.False(result.Ok);
        Assert.Contains("자동 생성", result.Error);
        Assert.Empty(_forge.CreatedVisibility);
    }

    [Fact]
    public async Task Reverse_sync_creates_github_repo_in_org()
    {
        await _forge.AddGitLabAsync("mirrors/internal");
        await Commit("i1");
        await Push(_forge.GitLabPath("mirrors/internal"), "main");

        using var svc = new SyncService(Options(o => o.GitHubOwner = "acme"), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "internal");
        var result = await svc.SyncAsync(pair, Provider.GitLab);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("acme/internal", pair.GitHub!.FullName);
        Assert.Equal(await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/internal")), await FakeForge.RefsAsync(_forge.GitHubPath("acme/internal")));
        Assert.Contains(_forge.RequestLog, r => r.StartsWith("POST /gh/api/orgs/acme/repos"));
    }

    [Fact]
    public async Task Reverse_sync_creates_github_repo_under_user_account()
    {
        await _forge.AddGitLabAsync("mirrors/tool");
        await Commit("t1");
        await Push(_forge.GitLabPath("mirrors/tool"), "main");

        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "tool");
        var result = await svc.SyncAsync(pair, Provider.GitLab);
        Assert.True(result.Ok, result.Error);
        Assert.Equal($"{FakeForge.GitHubLogin}/tool", pair.GitHub!.FullName);
        Assert.Equal(await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/tool")),
                     await FakeForge.RefsAsync(_forge.GitHubPath($"{FakeForge.GitHubLogin}/tool")));
        Assert.Contains(_forge.RequestLog, r => r.StartsWith("PATCH /gh/api/repos/octo/tool"));   // default branch set
    }

    [Fact]
    public async Task Gitlab_ahead_branch_is_fast_forwarded_by_reverse_sync()
    {
        await _forge.AddGitHubAsync("octo/rev");
        await _forge.AddGitLabAsync("mirrors/rev");
        await Commit("r1");
        await Push(_forge.GitHubPath("octo/rev"), "main");
        await Commit("r2");
        await Push(_forge.GitLabPath("mirrors/rev"), "main");
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "rev");

        var toGitLab = await svc.SyncAsync(pair, Provider.GitHub);        // GitLab is ahead → nothing to push, skipped
        Assert.Equal(0, toGitLab.Pushed);
        Assert.Equal(1, toGitLab.Skipped);
        var toGitHub = await svc.SyncAsync(pair, Provider.GitLab);
        Assert.Equal(1, toGitHub.Pushed);
        Assert.Equal(RepoStatus.Synced, pair.Status);
    }

    [Fact]
    public async Task Empty_source_repository_is_handled()
    {
        await _forge.AddGitHubAsync("octo/empty");
        await _forge.AddGitLabAsync("mirrors/empty");
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "empty");
        await svc.QuickCompareAsync(pair);
        Assert.Equal(RepoStatus.Empty, pair.Status);
        await svc.AnalyzeAsync(pair);
        Assert.Equal(RepoStatus.Empty, pair.Status);
        var r = await svc.SyncAsync(pair, Provider.GitHub);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, r.Pushed);
    }

    [Fact]
    public async Task Git_auth_failure_is_reported_and_marks_repo_as_error()
    {
        await SeedScenarioAsync();
        using var good = new SyncService(Options(), _log);
        var pair = (await good.ScanAsync()).Single(p => p.Name == "app");

        // API token fine for scan, but a wrong GitLab token for git operations.
        using var bad = new SyncService(Options(o => o.GitLabToken = "wrong"), _log);
        var ex = await Assert.ThrowsAsync<SyncException>(() => bad.AnalyzeAsync(pair));
        Assert.Contains("GitLab", ex.Message);
        Assert.Equal(RepoStatus.Error, pair.Status);
        Assert.NotNull(pair.Error);

        // A later successful analyze clears the error.
        await good.AnalyzeAsync(pair);
        Assert.Null(pair.Error);
        Assert.Equal(RepoStatus.OutOfSync, pair.Status);
    }

    [Fact]
    public async Task Tokens_are_never_written_to_the_cache_repository()
    {
        await SeedScenarioAsync();
        using var svc = new SyncService(Options(), _log);
        var pair = (await svc.ScanAsync()).Single(p => p.Name == "app");
        await svc.SyncAsync(pair, Provider.GitHub);

        foreach (var file in Directory.EnumerateFiles(_cache, "*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "objects" + Path.DirectorySeparatorChar)))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(FakeForge.GitHubToken, text);
            Assert.DoesNotContain(FakeForge.GitLabToken, text);
        }
        foreach (var e in _entries)
        {
            Assert.DoesNotContain(FakeForge.GitHubToken, e.Message + e.Detail);
            Assert.DoesNotContain(FakeForge.GitLabToken, e.Message + e.Detail);
        }
    }

    [Fact]
    public async Task Many_repositories_sync_in_parallel()
    {
        for (int i = 0; i < 8; i++)
        {
            await _forge.AddGitHubAsync($"octo/p{i}");
            if (i % 2 == 0) await _forge.AddGitLabAsync($"mirrors/p{i}");
            await Commit($"p{i}");
            await Push(_forge.GitHubPath($"octo/p{i}"), "main");
        }
        using var svc = new SyncService(Options(o => o.MaxParallel = 4), _log);
        var pairs = await svc.ScanAsync();
        var results = await Task.WhenAll(pairs.Select(p => svc.SyncAsync(p, Provider.GitHub)));
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(4, results.Count(r => r.CreatedRepo));
        for (int i = 0; i < 8; i++)
            Assert.Equal((await FakeForge.RefsAsync(_forge.GitHubPath($"octo/p{i}")))["refs/heads/main"],
                         (await FakeForge.RefsAsync(_forge.GitLabPath($"mirrors/p{i}")))["refs/heads/main"]);
        Assert.All(pairs, p => Assert.Equal(RepoStatus.Synced, p.Status));
    }
}
