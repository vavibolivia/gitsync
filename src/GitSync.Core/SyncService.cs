namespace GitSync.Core;

/// <summary>
/// High level operations shared by the desktop app and the server:
/// scan all repositories, compare branches, create missing repositories/branches and push.
/// </summary>
public sealed class SyncService : ISyncBackend
{
    private readonly GitMirror _git;

    public bool IsRemote => false;
    public string Location => "이 PC";
    public bool CreateMissingRepos => Options.CreateMissingRepos;
    public bool SyncTags => Options.SyncTags;
    public int MaxParallel => Math.Clamp(Options.MaxParallel, 1, 8);

    Task<List<RepoPair>> ISyncBackend.ScanAsync(IProgress<string>? progress, CancellationToken ct) => ScanAsync(ct);

    public async Task AnalyzeManyAsync(IReadOnlyList<RepoPair> pairs, IProgress<(string Message, double Percent)>? progress = null,
        CancellationToken ct = default)
    {
        var targets = pairs.Where(p => p.HasBoth).ToList();
        await ForEachAsync(targets, "정밀 분석", async p =>
        {
            try { await AnalyzeAsync(p, null, ct); } catch (SyncException) { /* logged, pair marked as error */ }
        }, progress, ct);
    }

    public async Task<List<SyncResult>> SyncManyAsync(IReadOnlyList<RepoPair> pairs, Provider from,
        IProgress<(string Message, double Percent)>? progress = null, CancellationToken ct = default)
    {
        var results = new List<SyncResult>();
        var targets = pairs.Where(p => p.Get(from) is not null).ToList();
        await ForEachAsync(targets, $"{from.Other().Label()}로 동기화", async p =>
        {
            var r = await SyncAsync(p, from, null, ct);
            lock (results) results.Add(r);
        }, progress, ct);
        Log.Write(results.All(r => r.Ok) ? SyncLogLevel.Success : SyncLogLevel.Warning,
            $"일괄 동기화 완료 · 생성 {results.Count(r => r.CreatedRepo)} · 브랜치 푸시 {results.Sum(r => r.Pushed)} · 실패 {results.Count(r => !r.Ok)}", "bulk");
        return results;
    }

    private async Task ForEachAsync(IReadOnlyList<RepoPair> items, string label, Func<RepoPair, Task> work,
        IProgress<(string, double)>? progress, CancellationToken ct)
    {
        if (items.Count == 0) return;
        int done = 0;
        progress?.Report(($"{label} 0/{items.Count}", 0));
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct },
            async (item, _) =>
            {
                await work(item);
                var n = Interlocked.Increment(ref done);
                progress?.Report(($"{label} {n}/{items.Count}", n * 100.0 / items.Count));
            });
    }

    public SyncOptions Options { get; }
    public SyncLogger Log { get; }
    public GitHubClient? GitHub { get; }
    public GitLabClient? GitLab { get; }

    public SyncService(SyncOptions options, SyncLogger log)
    {
        Options = options.Clone();
        Log = log;
        _git = new GitMirror(Options, log);
        if (Options.IsGitHubConfigured) GitHub = new GitHubClient(Options.GitHubApiUrl, Options.GitHubToken);
        if (Options.IsGitLabConfigured) GitLab = new GitLabClient(Options.GitLabUrl, Options.GitLabToken);
    }

    private GitHubClient RequireGitHub() => GitHub ?? throw new SyncException("GitHub 키(액세스 토큰)가 설정되지 않았습니다.");
    private GitLabClient RequireGitLab() => GitLab ?? throw new SyncException("GitLab 키(액세스 토큰)가 설정되지 않았습니다.");

    // ───────────────────────────── connection ─────────────────────────────

    public async Task<string> TestGitHubAsync(CancellationToken ct = default) => await RequireGitHub().GetLoginAsync(ct);
    public async Task<string> TestGitLabAsync(CancellationToken ct = default)
    {
        var user = await RequireGitLab().GetUsernameAsync(ct);
        var expected = Options.GitLabUser.Trim();
        if (expected.Length > 0 && !expected.Equals(user, StringComparison.OrdinalIgnoreCase))
            throw new SyncException($"GitLab 키의 계정({user})이 입력한 ID({expected})와 다릅니다.");
        return user;
    }

    /// <summary>Repositories seen by the last scan (used to pick pairs by hand).</summary>
    public IReadOnlyList<RemoteRepo> LastGitHubRepos { get; private set; } = [];
    public IReadOnlyList<RemoteRepo> LastGitLabRepos { get; private set; } = [];

    // ───────────────────────────── scan ─────────────────────────────

    /// <summary>Lists every repository on both sides and pairs them by name.</summary>
    public async Task<List<RepoPair>> ScanAsync(CancellationToken ct = default)
    {
        var gh = RequireGitHub();
        var gl = RequireGitLab();
        Log.Info("저장소 목록을 조회합니다…", "scan");

        var ghTask = gh.GetReposAsync(Options.GitHubOwner, Options.IncludeArchived, ct);
        var glTask = gl.GetProjectsAsync(Options.GitLabGroup, Options.IncludeArchived, ct);
        await Task.WhenAll(ghTask, glTask);

        LastGitHubRepos = ghTask.Result;
        LastGitLabRepos = glTask.Result;
        var pairs = Options.ManualPairs ? MatchManual(ghTask.Result, glTask.Result) : Match(ghTask.Result, glTask.Result);
        Log.Success($"GitHub {ghTask.Result.Count}개, GitLab {glTask.Result.Count}개 → {pairs.Count}개 항목 " +
                    $"(매칭 {pairs.Count(p => p.HasBoth)}, GitHub만 {pairs.Count(p => p.GitLab is null)}, GitLab만 {pairs.Count(p => p.GitHub is null)})", "scan");
        return pairs;
    }

    /// <summary>Builds only the pairs the user listed by hand (<see cref="SyncOptions.Pairs"/>).</summary>
    private List<RepoPair> MatchManual(List<RemoteRepo> ghRepos, List<RemoteRepo> glRepos)
    {
        static RemoteRepo? Find(List<RemoteRepo> repos, string key) =>
            key.Length == 0 ? null
            : repos.FirstOrDefault(r => r.FullName.Equals(key, StringComparison.OrdinalIgnoreCase))
              ?? (key.Contains('/') ? null : repos.FirstOrDefault(r => r.Name.Equals(key, StringComparison.OrdinalIgnoreCase)));

        var result = new List<RepoPair>();
        foreach (var (ghKey, glKey) in Options.ParsePairs())
        {
            var gh = Find(ghRepos, ghKey);
            // An empty side means "same name as the other side" (created on sync when it does not exist yet).
            var gl = Find(glRepos, glKey.Length > 0 ? glKey : gh?.Name ?? "");
            if (gh is null && glKey.Length > 0 && ghKey.Length == 0 && gl is not null)
                gh = Find(ghRepos, gl.Name);
            if (gh is null && gl is null)
            {
                Log.Warn($"'{ghKey}' ⇄ '{glKey}': 양쪽 모두에서 저장소를 찾지 못했습니다.", "scan");
                continue;
            }
            var id = gh is not null ? "gh_" + gh.FullName : "gl_" + gl!.FullName;
            if (result.Any(p => p.Id == id)) continue;
            result.Add(new RepoPair { Id = id, Name = gh?.Name ?? gl!.Name, GitHub = gh, GitLab = gl });
        }
        foreach (var p in result) UpdateStatus(p);
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private List<RepoPair> Match(List<RemoteRepo> ghRepos, List<RemoteRepo> glRepos)
    {
        var mappings = Options.ParseMappings();
        var targetNs = Options.EffectiveTargetNamespace;
        var glByPath = glRepos.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<RemoteRepo>();
        var result = new List<RepoPair>();

        foreach (var gh in ghRepos)
        {
            var key = mappings.TryGetValue(gh.Name, out var mapped) ? mapped : gh.Name;
            RemoteRepo? match = null;

            // A mapping may be a full path (group/sub/project) or just a project path.
            if (key.Contains('/'))
                match = glRepos.FirstOrDefault(r => !used.Contains(r) && r.FullName.Equals(key, StringComparison.OrdinalIgnoreCase));
            else if (glByPath.TryGetValue(key, out var candidates))
            {
                var free = candidates.Where(c => !used.Contains(c)).ToList();
                match = free.FirstOrDefault(c => targetNs.Length > 0 &&
                            c.FullName.StartsWith(targetNs + "/", StringComparison.OrdinalIgnoreCase))
                        ?? free.FirstOrDefault();
                if (free.Count > 1)
                    Log.Warn($"'{gh.Name}'과 이름이 같은 GitLab 프로젝트가 {free.Count}개 있어 {match!.FullName}을(를) 사용합니다. " +
                             "그룹 필터나 이름 매핑으로 지정할 수 있습니다.", "scan");
            }
            if (match is not null) used.Add(match);
            result.Add(new RepoPair { Id = "gh_" + gh.FullName, Name = gh.Name, GitHub = gh, GitLab = match });
        }

        foreach (var gl in glRepos.Where(r => !used.Contains(r)))
            result.Add(new RepoPair { Id = "gl_" + gl.FullName, Name = gl.Name, GitLab = gl });

        foreach (var p in result) UpdateStatus(p);
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ───────────────────────────── compare ─────────────────────────────

    /// <summary>Fast comparison through the REST APIs (branch head SHAs only, no cloning).</summary>
    public async Task QuickCompareAsync(RepoPair pair, CancellationToken ct = default)
    {
        if (!pair.HasBoth) { UpdateStatus(pair); return; }
        try
        {
            var t1 = RequireGitHub().GetBranchesAsync(pair.GitHub!.FullName, ct);
            var t2 = RequireGitLab().GetBranchesAsync(pair.GitLab!.Id, ct);
            await Task.WhenAll(t1, t2);

            var gh = t1.Result.ToDictionary(b => b.Name, b => b.Sha, StringComparer.Ordinal);
            var gl = t2.Result.ToDictionary(b => b.Name, b => b.Sha, StringComparer.Ordinal);
            var rows = new List<BranchState>();
            foreach (var name in gh.Keys.Union(gl.Keys))
            {
                gh.TryGetValue(name, out var a);
                gl.TryGetValue(name, out var b);
                rows.Add(new BranchState
                {
                    Name = name,
                    IsDefault = name == pair.DefaultBranch,
                    GitHubSha = a,
                    GitLabSha = b,
                    Status = a is null ? BranchStatus.GitLabOnly
                           : b is null ? BranchStatus.GitHubOnly
                           : a == b ? BranchStatus.Synced : BranchStatus.Differs,
                });
            }
            pair.Branches = Sort(rows);
            pair.IsAnalyzed = false;
            pair.Error = null;
            pair.LastChecked = DateTimeOffset.Now;
            UpdateStatus(pair);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(pair, ex);
        }
    }

    /// <summary>Fetches both sides into the local cache and computes exact ahead/behind counts.</summary>
    public async Task AnalyzeAsync(RepoPair pair, IProgress<string>? progress = null, CancellationToken ct = default,
        Provider? fetchOnly = null)
    {
        if (!pair.HasBoth) { UpdateStatus(pair); return; }
        var scope = pair.Name;
        try
        {
            var dir = await _git.EnsureAsync(pair.Id, ct);
            foreach (var p in new[] { Provider.GitHub, Provider.GitLab })
            {
                if (fetchOnly is not null && fetchOnly != p) continue;
                progress?.Report($"{p.Label()}에서 가져오는 중…");
                await _git.FetchAsync(dir, pair.Get(p)!, scope, ct);
            }
            progress?.Report("비교 중…");
            pair.Error = null;
            await CompareLocalAsync(pair, dir, ct);
            pair.Error = null;
            Log.Info(pair.Status == RepoStatus.Synced
                ? "분석 완료 · 모든 브랜치 동일"
                : $"분석 완료 · 차이 {pair.DiffCount}개 브랜치", scope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(pair, ex);
            throw;
        }
    }

    private async Task CompareLocalAsync(RepoPair pair, string dir, CancellationToken ct)
    {
        var gh = await _git.ReadHeadsAsync(dir, Provider.GitHub, ct);
        var gl = await _git.ReadHeadsAsync(dir, Provider.GitLab, ct);
        var rows = new List<BranchState>();
        foreach (var name in gh.Keys.Union(gl.Keys))
        {
            gh.TryGetValue(name, out var a);
            gl.TryGetValue(name, out var b);
            int ghAhead = 0, glAhead = 0;
            BranchStatus status;
            if (a is null) status = BranchStatus.GitLabOnly;
            else if (b is null) status = BranchStatus.GitHubOnly;
            else if (a.Sha == b.Sha) status = BranchStatus.Synced;
            else
            {
                (ghAhead, glAhead) = await _git.CountAsync(dir, name, ct);
                status = glAhead == 0 ? BranchStatus.GitHubAhead
                       : ghAhead == 0 ? BranchStatus.GitLabAhead
                       : BranchStatus.Diverged;
            }
            rows.Add(new BranchState
            {
                Name = name,
                IsDefault = name == pair.DefaultBranch,
                GitHubSha = a?.Sha,
                GitLabSha = b?.Sha,
                GitHubCommit = a,
                GitLabCommit = b,
                GitHubAhead = ghAhead,
                GitLabAhead = glAhead,
                Status = status,
            });
        }

        var ghTags = await _git.ReadTagsAsync(dir, Provider.GitHub, ct);
        var glTags = await _git.ReadTagsAsync(dir, Provider.GitLab, ct);
        int same = 0, onlyGh = 0, onlyGl = 0, diff = 0;
        foreach (var t in ghTags.Keys.Union(glTags.Keys))
        {
            var inGh = ghTags.TryGetValue(t, out var x);
            var inGl = glTags.TryGetValue(t, out var y);
            if (inGh && inGl) { if (x == y) same++; else diff++; }
            else if (inGh) onlyGh++;
            else onlyGl++;
        }

        pair.Branches = Sort(rows);
        pair.Tags = new TagSummary(same, onlyGh, onlyGl, diff);
        pair.IsAnalyzed = true;
        pair.LastChecked = DateTimeOffset.Now;
        UpdateStatus(pair);
    }

    // ───────────────────────────── sync ─────────────────────────────

    /// <summary>What a non-destructive sync from <paramref name="from"/> would do. Requires an analyzed pair.</summary>
    public static SyncPlan BuildPlan(RepoPair pair, Provider from)
    {
        var plan = new SyncPlan { From = from };
        var to = from.Other();
        foreach (var b in pair.Branches)
        {
            if (b.Sha(from) is null) continue;                 // only on target: leave it alone
            if (b.Sha(to) is null)
                plan.Actions.Add(new BranchAction(b.Name, BranchActionKind.Create, 0));
            else if (b.Status == BranchStatus.Synced)
                continue;
            else if (b.Ahead(to) == 0 && b.Ahead(from) > 0)
                plan.Actions.Add(new BranchAction(b.Name, BranchActionKind.FastForward, b.Ahead(from)));
            else
                plan.Actions.Add(new BranchAction(b.Name, BranchActionKind.Skip, b.Ahead(from),
                    b.Status == BranchStatus.Diverged ? "분기됨 (강제 푸시 필요)" : $"{to.Label()} 쪽이 앞섬"));
        }
        // Default branch first: on a freshly created repository it becomes the default.
        plan.Actions.Sort((x, y) => (y.Branch == pair.DefaultBranch).CompareTo(x.Branch == pair.DefaultBranch));
        return plan;
    }

    /// <summary>
    /// Creates the target repository if missing, then pushes every new branch and every branch
    /// that can be fast-forwarded (plus tags). Diverged branches are never overwritten here.
    /// </summary>
    public async Task<SyncResult> SyncAsync(RepoPair pair, Provider from, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var to = from.Other();
        var scope = pair.Name;
        var source = pair.Get(from);
        if (source is null) return new SyncResult(pair.Name, false, 0, 0, 0, $"{from.Label()} 저장소가 없습니다.");

        try
        {
            bool created = false;
            if (pair.Get(to) is null)
            {
                if (!Options.CreateMissingRepos)
                    return new SyncResult(pair.Name, false, 0, 0, 0, $"{to.Label()}에 저장소가 없고 자동 생성이 꺼져 있습니다.");
                progress?.Report($"{to.Label()}에 저장소 생성 중…");
                pair.Set(to, await CreateRepoAsync(pair, to, source, ct));
                created = true;
            }

            await AnalyzeAsync(pair, progress, ct);
            var plan = BuildPlan(pair, from);
            var pushes = plan.Pushes.Select(a => a.Branch).ToList();
            int pushed = 0, failed = 0;
            var target = pair.Get(to)!;
            var dir = await _git.EnsureAsync(pair.Id, ct);

            foreach (var chunk in pushes.Chunk(40))
            {
                progress?.Report($"{to.Label()}로 푸시 중… ({pushed + chunk.Length}/{pushes.Count})");
                var r = await _git.PushAsync(dir, target, from, chunk, force: false, scope, ct);
                if (r.Ok) pushed += chunk.Length;
                else
                {
                    var rejected = CountRejected(r.StdOut);
                    failed += Math.Max(1, rejected);
                    pushed += Math.Max(0, chunk.Length - Math.Max(1, rejected));
                    Log.Error("일부 브랜치 푸시 실패", scope, (r.StdErr + "\n" + r.StdOut).Trim());
                }
            }

            if (Options.SyncTags)
            {
                var tagsToPush = await MissingTagsAsync(dir, from, ct);
                foreach (var chunk in tagsToPush.Chunk(200))
                {
                    progress?.Report($"태그 푸시 중… ({chunk.Length}개)");
                    var r = await _git.PushTagsAsync(dir, target, from, chunk, scope, ct);
                    if (!r.Ok) Log.Warn("일부 태그 푸시 실패", scope, (r.StdErr + "\n" + r.StdOut).Trim());
                }
                if (tagsToPush.Count > 0) Log.Info($"태그 {tagsToPush.Count}개 푸시", scope);
            }

            if (created && source.DefaultBranch is { Length: > 0 } def && pushes.Contains(def))
                await TrySetDefaultBranchAsync(target, def, ct);

            if (pushes.Count > 0 || created)
                await AnalyzeAsync(pair, progress, ct, fetchOnly: to);

            var skipped = plan.Skipped.Count();
            foreach (var s in plan.Skipped)
                Log.Warn($"'{s.Branch}' 건너뜀: {s.Reason}", scope);

            var result = new SyncResult(pair.Name, created, pushed, skipped, failed);
            if (pushes.Count == 0 && !created)
                Log.Info($"{from.Label()} → {to.Label()}: 푸시할 변경 없음" + (skipped > 0 ? $" (건너뜀 {skipped})" : ""), scope);
            else
                Log.Write(failed == 0 ? SyncLogLevel.Success : SyncLogLevel.Warning,
                    $"{from.Label()} → {to.Label()} 동기화: {(created ? "저장소 생성, " : "")}브랜치 {pushed}개 푸시" +
                    (skipped > 0 ? $", 건너뜀 {skipped}" : "") + (failed > 0 ? $", 실패 {failed}" : ""), scope);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(pair, ex);
            return new SyncResult(pair.Name, false, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>Pushes a single branch. <paramref name="force"/> is required when the target has commits the source lacks.</summary>
    public async Task PushBranchAsync(RepoPair pair, string branch, Provider from, bool force,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var to = from.Other();
        var target = pair.Get(to) ?? throw new SyncException($"{to.Label()} 저장소가 없습니다.");
        var dir = await _git.EnsureAsync(pair.Id, ct);
        progress?.Report($"'{branch}' → {to.Label()} 푸시 중…");
        var r = await _git.PushAsync(dir, target, from, [branch], force, pair.Name, ct);
        if (!r.Ok)
            throw new SyncException($"'{branch}' 푸시 실패", (r.StdErr + "\n" + r.StdOut).Trim());
        Log.Success($"'{branch}' {from.Label()} → {to.Label()} {(force ? "강제 " : "")}푸시 완료", pair.Name);
        await CompareLocalAsync(pair, dir, ct);
    }

    /// <summary>True when pushing this branch from <paramref name="from"/> would discard commits on the target.</summary>
    public static bool RequiresForce(BranchState b, Provider from) =>
        b.Sha(from.Other()) is not null && b.Ahead(from.Other()) > 0;

    private async Task<List<string>> MissingTagsAsync(string dir, Provider from, CancellationToken ct)
    {
        var src = await _git.ReadTagsAsync(dir, from, ct);
        var dst = await _git.ReadTagsAsync(dir, from.Other(), ct);
        return src.Keys.Where(t => !dst.ContainsKey(t)).ToList();
    }

    private async Task<RemoteRepo> CreateRepoAsync(RepoPair pair, Provider to, RemoteRepo source, CancellationToken ct)
    {
        RemoteRepo created;
        if (to == Provider.GitLab)
        {
            var visibility = Options.GitLabVisibility switch
            {
                "source" => source.IsPrivate ? "private" : "public",
                "internal" or "public" or "private" => Options.GitLabVisibility,
                _ => "private",
            };
            var ns = Options.EffectiveTargetNamespace;
            created = await RequireGitLab().CreateProjectAsync(source.Name, ns, visibility, source.Description, ct);
            Log.Success($"GitLab 프로젝트 생성: {created.FullName} ({visibility})", pair.Name);
        }
        else
        {
            created = await RequireGitHub().CreateRepoAsync(source.Name, Options.GitHubOwner, source.IsPrivate, source.Description, ct);
            Log.Success($"GitHub 저장소 생성: {created.FullName}", pair.Name);
        }
        return created;
    }

    private async Task TrySetDefaultBranchAsync(RemoteRepo target, string branch, CancellationToken ct)
    {
        try
        {
            if (target.Provider == Provider.GitLab) await RequireGitLab().SetDefaultBranchAsync(target.Id, branch, ct);
            else await RequireGitHub().SetDefaultBranchAsync(target.FullName, branch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"기본 브랜치 설정 실패: {ex.Message}", target.Name);
        }
    }

    private static int CountRejected(string porcelain) =>
        porcelain.Split('\n').Count(l => l.StartsWith('!'));

    // ───────────────────────────── helpers ─────────────────────────────

    private void Fail(RepoPair pair, Exception ex)
    {
        pair.Status = RepoStatus.Error;
        pair.Error = ex.Message;
        Log.Error(ex.Message, pair.Name, (ex as SyncException)?.Detail);
    }

    private static IReadOnlyList<BranchState> Sort(List<BranchState> rows) =>
        rows.OrderByDescending(r => r.IsDefault)
            .ThenBy(r => r.Status == BranchStatus.Synced)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static void UpdateStatus(RepoPair pair)
    {
        if (pair.GitHub is null) pair.Status = RepoStatus.GitLabOnly;
        else if (pair.GitLab is null) pair.Status = RepoStatus.GitHubOnly;
        else if (pair.Error is not null) pair.Status = RepoStatus.Error;
        else if (pair.LastChecked is null) pair.Status = RepoStatus.Unknown;
        else if (pair.Branches.Count == 0) pair.Status = RepoStatus.Empty;
        else if (pair.Branches.All(b => b.Status == BranchStatus.Synced) && (pair.Tags?.InSync ?? true))
            pair.Status = RepoStatus.Synced;
        else pair.Status = RepoStatus.OutOfSync;
    }

    public void Dispose()
    {
        GitHub?.Dispose();
        GitLab?.Dispose();
    }
}
