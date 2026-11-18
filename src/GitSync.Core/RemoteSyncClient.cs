using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitSync.Core;

/// <summary>
/// <see cref="ISyncBackend"/> that drives a GitSync server over its HTTP API. All git work happens on the server;
/// this client starts jobs, waits for them, and maps results back to <see cref="RepoPair"/> objects.
/// </summary>
public sealed class RemoteSyncClient : ISyncBackend
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SyncLogger _log;
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private long _lastLogId;
    private RemoteSettings? _settings;

    public RemoteSyncClient(string serverUrl, string accessKey, SyncLogger log)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new SyncException("서버 주소가 올바르지 않습니다. 예: http://10.0.0.5:8080");
        _http = new HttpClient { BaseAddress = new Uri(uri.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(accessKey)) _http.DefaultRequestHeaders.Add("X-Access-Key", accessKey.Trim());
        _log = log;
        Location = uri.Authority;
    }

    public bool IsRemote => true;
    public string Location { get; }
    public bool CreateMissingRepos => _settings?.CreateMissingRepos ?? true;
    public bool SyncTags => _settings?.SyncTags ?? true;
    public int MaxParallel => _settings?.MaxParallel ?? 3;

    // ───────────────────────── DTOs ─────────────────────────

    public sealed class RemoteSettings
    {
        public string? GitHubApiUrl { get; set; }
        public string? GitHubOwner { get; set; }
        public bool HasGitHubToken { get; set; }
        public string? GitLabUrl { get; set; }
        public string? GitLabGroup { get; set; }
        public string? GitLabTargetNamespace { get; set; }
        public string? GitLabVisibility { get; set; }
        public bool HasGitLabToken { get; set; }
        public bool SyncTags { get; set; } = true;
        public bool CreateMissingRepos { get; set; } = true;
        public bool IncludeArchived { get; set; }
        public int MaxParallel { get; set; } = 3;
        public string? NameMappings { get; set; }
        public int AutoSyncMinutes { get; set; }
    }

    public sealed record RemoteStatus(bool Configured, string? GithubUser, string? GitlabUser, RemoteJob? Job, int AutoSyncMinutes);
    public sealed record RemoteJob(string Name, bool Running, string Message, double? Progress, string? Error);
    private sealed record RepoDto(string FullName, string? WebUrl, string? DefaultBranch, bool IsPrivate, string? Description);
    private sealed record PairDto(string Id, string Name, RepoStatus Status, bool IsAnalyzed, DateTimeOffset? LastChecked, string? Error,
        TagSummary? Tags, RepoDto? GitHub, RepoDto? GitLab, List<BranchState>? Branches);
    private sealed record TestDto(bool Ok, string? User, string? Error);
    private sealed record ErrorDto(string? Error);

    // ───────────────────────── HTTP helpers ─────────────────────────

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path.TrimStart('/'));
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new SyncException($"GitSync 서버({Location})에 연결할 수 없습니다: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new SyncException($"GitSync 서버({Location}) 응답 시간이 초과되었습니다."); }
        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new SyncException("GitSync 서버 접근 키가 올바르지 않습니다.");
            if (!resp.IsSuccessStatusCode)
            {
                ErrorDto? err = null;
                try { err = await resp.Content.ReadFromJsonAsync<ErrorDto>(Json, ct); } catch { }
                throw new SyncException(err?.Error ?? $"GitSync 서버 오류 ({(int)resp.StatusCode})");
            }
            if (typeof(T) == typeof(bool)) return (T)(object)true;
            return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    public Task<RemoteStatus> GetStatusAsync(CancellationToken ct = default) => SendAsync<RemoteStatus>(HttpMethod.Get, "api/status", null, ct);

    public async Task<RemoteSettings> GetSettingsAsync(CancellationToken ct = default) =>
        _settings = await SendAsync<RemoteSettings>(HttpMethod.Get, "api/settings", null, ct);

    /// <summary>Saves settings on the server. Null/empty tokens keep the server's stored tokens.</summary>
    public async Task<RemoteSettings> SaveSettingsAsync(object input, CancellationToken ct = default) =>
        _settings = await SendAsync<RemoteSettings>(HttpMethod.Put, "api/settings", input, ct);

    public async Task<string> TestAsync(string provider, object? input, CancellationToken ct = default)
    {
        var r = await SendAsync<TestDto>(HttpMethod.Post, $"api/test/{provider}", input ?? new { }, ct);
        return r.Ok ? r.User ?? "" : throw new SyncException(r.Error ?? "연결 실패");
    }

    public Task<string> TestGitHubAsync(CancellationToken ct = default) => TestAsync("github", null, ct);
    public Task<string> TestGitLabAsync(CancellationToken ct = default) => TestAsync("gitlab", null, ct);

    /// <summary>Copies new server log entries into the local logger.</summary>
    public async Task PullLogsAsync(CancellationToken ct = default)
    {
        var entries = await SendAsync<List<LogEntry>>(HttpMethod.Get, $"api/logs?after={_lastLogId}", null, ct);
        foreach (var e in entries)
        {
            _lastLogId = Math.Max(_lastLogId, e.Id);
            _log.Write(e.Level, e.Message, e.Scope, e.Detail);
        }
    }

    /// <summary>Starts a server job and waits for it. Throws if the job fails.</summary>
    private async Task RunJobAsync(string path, object? body, Action<string, double?>? report, CancellationToken ct)
    {
        await _jobGate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await SendAsync<bool>(HttpMethod.Post, path, body ?? new { }, ct);
                    report?.Invoke($"서버({Location})에서 작업 시작…", null);
                    break;
                }
                catch (SyncException ex) when (attempt < 60 && ex.Message.Contains("다른 작업"))
                {
                    report?.Invoke("서버에서 다른 작업이 끝나기를 기다리는 중…", null);
                    await Task.Delay(1000, ct);
                }
            }
            while (true)
            {
                await Task.Delay(700, ct);
                RemoteStatus s;
                try { s = await GetStatusAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    try { await SendAsync<bool>(HttpMethod.Post, "api/cancel", null, CancellationToken.None); } catch { }
                    throw;
                }
                if (s.Job is { Running: true } j) { report?.Invoke(j.Message, j.Progress); continue; }
                try { await PullLogsAsync(ct); } catch { /* logs are best effort */ }
                if (s.Job?.Error is { } err) throw new SyncException(err);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            try { await SendAsync<bool>(HttpMethod.Post, "api/cancel", null, CancellationToken.None); } catch { }
            throw;
        }
        finally { _jobGate.Release(); }
    }

    private static Action<string, double?>? Wrap(IProgress<string>? p) => p is null ? null : (m, _) => p.Report(m);
    private static Action<string, double?>? Wrap(IProgress<(string, double)>? p) => p is null ? null : (m, pct) => p.Report((m, pct ?? 0));

    // ───────────────────────── mapping ─────────────────────────

    private static RemoteRepo? Map(RepoDto? r, Provider p) => r is null ? null : new RemoteRepo(
        p, 0, r.FullName[(r.FullName.LastIndexOf('/') + 1)..], r.FullName, r.WebUrl ?? "", "", r.DefaultBranch, null, r.IsPrivate, r.Description);

    private static RepoPair ToPair(PairDto d) => new()
    {
        Id = d.Id, Name = d.Name, GitHub = Map(d.GitHub, Provider.GitHub), GitLab = Map(d.GitLab, Provider.GitLab),
        Status = d.Status, IsAnalyzed = d.IsAnalyzed, LastChecked = d.LastChecked, Error = d.Error, Tags = d.Tags,
        Branches = d.Branches ?? [],
    };

    private static void Apply(RepoPair target, PairDto d)
    {
        var src = ToPair(d);
        target.GitHub = src.GitHub;
        target.GitLab = src.GitLab;
        target.Status = src.Status;
        target.IsAnalyzed = src.IsAnalyzed;
        target.LastChecked = src.LastChecked;
        target.Error = src.Error;
        target.Tags = src.Tags;
        target.Branches = src.Branches;
    }

    private async Task RefreshAsync(RepoPair pair, CancellationToken ct) =>
        Apply(pair, await SendAsync<PairDto>(HttpMethod.Get, "api/repo?id=" + Uri.EscapeDataString(pair.Id), null, ct));

    private async Task RefreshAllAsync(IEnumerable<RepoPair> pairs, CancellationToken ct)
    {
        var all = (await SendAsync<List<PairDto>>(HttpMethod.Get, "api/repos?full=true", null, ct)).ToDictionary(d => d.Id);
        foreach (var p in pairs)
            if (all.TryGetValue(p.Id, out var d)) Apply(p, d);
    }

    // ───────────────────────── ISyncBackend ─────────────────────────

    public async Task<List<RepoPair>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await GetSettingsAsync(ct);
        await RunJobAsync("api/scan", null, Wrap(progress), ct);
        var list = await SendAsync<List<PairDto>>(HttpMethod.Get, "api/repos?full=true", null, ct);
        return list.Select(ToPair).ToList();
    }

    public Task QuickCompareAsync(RepoPair pair, CancellationToken ct = default) => RefreshAsync(pair, ct);

    public async Task AnalyzeAsync(RepoPair pair, IProgress<string>? progress = null, CancellationToken ct = default, Provider? fetchOnly = null)
    {
        try
        {
            await RunJobAsync("api/analyze", new { ids = new[] { pair.Id } }, Wrap(progress), ct);
        }
        finally { await RefreshAsync(pair, ct); }
        if (pair.Error is { } e) throw new SyncException(e);
    }

    public async Task<SyncResult> SyncAsync(RepoPair pair, Provider from, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var to = from.Other();
        var hadTarget = pair.Get(to) is not null;
        var before = pair.Branches.ToDictionary(b => b.Name, b => b.Sha(to));
        try
        {
            await RunJobAsync("api/sync", new { ids = new[] { pair.Id }, from = from.ToString().ToLowerInvariant() }, Wrap(progress), ct);
        }
        catch (SyncException ex)
        {
            await RefreshAsync(pair, ct);
            return new SyncResult(pair.Name, false, 0, 0, 1, ex.Message);
        }
        await RefreshAsync(pair, ct);
        var pushed = pair.Branches.Count(b => b.Sha(from) is not null && b.Sha(to) == b.Sha(from)
                                              && (!before.TryGetValue(b.Name, out var old) || old != b.Sha(to)));
        var skipped = SyncService.BuildPlan(pair, from).Skipped.Count();
        return new SyncResult(pair.Name, !hadTarget && pair.Get(to) is not null, pushed, skipped, 0, pair.Error);
    }

    public async Task PushBranchAsync(RepoPair pair, string branch, Provider from, bool force, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            await RunJobAsync("api/push", new { id = pair.Id, branch, from = from.ToString().ToLowerInvariant(), force }, Wrap(progress), ct);
        }
        finally { await RefreshAsync(pair, ct); }
    }

    public async Task AnalyzeManyAsync(IReadOnlyList<RepoPair> pairs, IProgress<(string Message, double Percent)>? progress = null, CancellationToken ct = default)
    {
        try { await RunJobAsync("api/analyze", new { ids = pairs.Select(p => p.Id).ToArray() }, Wrap(progress), ct); }
        finally { await RefreshAllAsync(pairs, ct); }
    }

    public async Task<List<SyncResult>> SyncManyAsync(IReadOnlyList<RepoPair> pairs, Provider from, IProgress<(string Message, double Percent)>? progress = null, CancellationToken ct = default)
    {
        var to = from.Other();
        var snapshot = pairs.ToDictionary(p => p.Id, p => (HadTarget: p.Get(to) is not null, Before: p.Branches.ToDictionary(b => b.Name, b => b.Sha(to))));
        string? error = null;
        try { await RunJobAsync("api/sync", new { ids = pairs.Select(p => p.Id).ToArray(), from = from.ToString().ToLowerInvariant() }, Wrap(progress), ct); }
        catch (SyncException ex) { error = ex.Message; }
        await RefreshAllAsync(pairs, ct);
        return pairs.Select(p =>
        {
            var s = snapshot[p.Id];
            var pushed = p.Branches.Count(b => b.Sha(from) is not null && b.Sha(to) == b.Sha(from)
                                               && (!s.Before.TryGetValue(b.Name, out var old) || old != b.Sha(to)));
            return new SyncResult(p.Name, !s.HadTarget && p.Get(to) is not null, pushed, 0, p.Error is null ? 0 : 1, p.Error ?? error);
        }).ToList();
    }

    public void Dispose()
    {
        _http.Dispose();
        _jobGate.Dispose();
    }
}
