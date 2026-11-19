using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using GitSync.Core;
using GitSync.Server;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Environment.GetEnvironmentVariable("GITSYNC_DATA") is { Length: > 0 } d
    ? d
    : (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "data") : "/data");
var accessKey = Environment.GetEnvironmentVariable("GITSYNC_ACCESS_KEY") ?? "";

builder.Services.AddSingleton(new SettingsFile(dataDir));
builder.Services.AddSingleton<SyncHost>();
builder.Services.AddHostedService<AutoSyncWorker>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

if (accessKey.Length == 0)
    app.Logger.LogWarning("GITSYNC_ACCESS_KEY is not set: the API is open to anyone who can reach it.");

// Optional: serve the web UI directly (development without nginx).
if (Environment.GetEnvironmentVariable("GITSYNC_WEB_ROOT") is { Length: > 0 } webRoot)
{
    // A relative path resolves against the working directory, which `dotnet run --project` sets to the project folder.
    var webRootFull = Path.GetFullPath(webRoot);
    if (Directory.Exists(webRootFull))
    {
        var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRootFull);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
    }
    else
        app.Logger.LogWarning("GITSYNC_WEB_ROOT directory not found, the web UI is not served: {Path}", webRootFull);
}

// Access key check for every /api call except health.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (accessKey.Length > 0 && path.StartsWith("/api") && path != "/api/health")
    {
        var given = ctx.Request.Headers["X-Access-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(accessKey)))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "접근 키가 필요합니다." });
            return;
        }
    }
    await next();
});

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { ok = true }));

api.MapGet("/status", (SyncHost h) => new
{
    configured = h.IsConfigured,
    githubUser = h.GitHubUser,
    gitlabUser = h.GitLabUser,
    githubHost = Host(h.Settings.Sync.GitHubApiUrl),
    gitlabHost = Host(h.Settings.Sync.GitLabUrl),
    job = h.Job,
    lastScan = h.LastScan,
    autoSyncMinutes = h.Settings.AutoSyncMinutes,
    nextAutoSync = h.NextAutoSync,
    counts = new
    {
        total = h.Pairs.Count,
        synced = h.Pairs.Count(p => p.Status is RepoStatus.Synced or RepoStatus.Empty),
        outOfSync = h.Pairs.Count(p => p.Status is RepoStatus.OutOfSync or RepoStatus.Unknown or RepoStatus.Error),
        githubOnly = h.Pairs.Count(p => p.Status == RepoStatus.GitHubOnly),
        gitlabOnly = h.Pairs.Count(p => p.Status == RepoStatus.GitLabOnly),
    },
    lastLogId = h.Log.Since(0, 1).LastOrDefault()?.Id ?? 0,
});

// ───────────────────────── settings ─────────────────────────

api.MapGet("/settings", (SyncHost h) => SettingsView(h.Settings));

api.MapPut("/settings", (SyncHost h, SettingsInput input) =>
{
    var cur = h.Settings;
    var next = new ServerSettings { Sync = input.Apply(cur.Sync), AutoSyncMinutes = Math.Max(0, input.AutoSyncMinutes ?? cur.AutoSyncMinutes) };
    try { h.UpdateSettings(next); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    return Results.Ok(SettingsView(h.Settings));
});

api.MapPost("/test/{provider}", async (SyncHost h, string provider, SettingsInput? input) =>
{
    using var svc = h.CreateService(input?.Apply(h.Settings.Sync) ?? h.Settings.Sync);
    try
    {
        var user = provider.Equals("github", StringComparison.OrdinalIgnoreCase)
            ? await svc.TestGitHubAsync()
            : await svc.TestGitLabAsync();
        return Results.Ok(new { ok = true, user });
    }
    catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
});

// ───────────────────────── repositories ─────────────────────────

api.MapGet("/repos", (SyncHost h, bool? full) => full == true ? h.Pairs.Select(PairDetail) : h.Pairs.Select(PairSummary));

api.MapGet("/repo", (SyncHost h, string id) =>
    h.Find(id) is { } p ? Results.Ok(PairDetail(p)) : Results.NotFound(new { error = "저장소를 찾을 수 없습니다." }));

// ───────────────────────── jobs ─────────────────────────

api.MapPost("/scan", (SyncHost h) =>
    !h.IsConfigured ? Results.BadRequest(new { error = "GitHub와 GitLab 토큰을 먼저 설정하세요." })
    : Start(h.TryStart("전체 검색", h.ScanAsync)));

api.MapPost("/analyze", (SyncHost h, TargetInput? input) =>
{
    var targets = Targets(h, input?.Ids);
    return Start(h.TryStart("정밀 분석", (svc, r, ct) => h.AnalyzeAsync(svc, targets, r, ct)));
});

api.MapPost("/sync", (SyncHost h, TargetInput? input) =>
{
    var from = ParseFrom(input?.From);
    var targets = Targets(h, input?.Ids);
    return Start(h.TryStart($"{from.Other().Label()}로 동기화", (svc, r, ct) => h.SyncAsync(svc, targets, from, r, ct)));
});

api.MapPost("/push", (SyncHost h, PushInput input) =>
{
    if (h.Find(input.Id) is not { } pair) return Results.NotFound(new { error = "저장소를 찾을 수 없습니다." });
    var from = ParseFrom(input.From);
    return Start(h.TryStart($"'{input.Branch}' 푸시", async (svc, report, ct) =>
    {
        if (!pair.IsAnalyzed) await svc.AnalyzeAsync(pair, null, ct);
        var state = pair.Branches.FirstOrDefault(b => b.Name == input.Branch)
                    ?? throw new SyncException($"'{input.Branch}' 브랜치를 찾을 수 없습니다.");
        var needsForce = SyncService.RequiresForce(state, from);
        if (needsForce && !input.Force)
            throw new SyncException($"'{input.Branch}'는 대상 쪽에 다른 커밋이 있어 강제 푸시가 필요합니다.");
        report($"'{input.Branch}' 푸시 중…", null);
        await svc.PushBranchAsync(pair, input.Branch, from, needsForce, null, ct);
    }));
});

api.MapPost("/cancel", (SyncHost h) => { h.Cancel(); return Results.Ok(); });

api.MapGet("/logs", (SyncHost h, long? after) => h.Log.Since(after ?? 0, 500));

app.Run();

// ───────────────────────── helpers ─────────────────────────

static IResult Start(bool started) => started
    ? Results.Accepted(value: new { started = true })
    : Results.Conflict(new { error = "다른 작업이 실행 중입니다." });

static Provider ParseFrom(string? from) =>
    string.Equals(from, "gitlab", StringComparison.OrdinalIgnoreCase) ? Provider.GitLab : Provider.GitHub;

static IReadOnlyList<RepoPair> Targets(SyncHost h, string[]? ids) =>
    ids is { Length: > 0 } ? h.Pairs.Where(p => ids.Contains(p.Id)).ToList() : h.Pairs.ToList();

static string Host(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host.StartsWith("api.") ? u.Host[4..] : u.Host) : url;

static object SettingsView(ServerSettings s) => new
{
    s.Sync.GitHubApiUrl,
    s.Sync.GitHubOwner,
    HasGitHubToken = s.Sync.IsGitHubConfigured,
    s.Sync.GitLabUrl,
    s.Sync.GitLabGroup,
    s.Sync.GitLabTargetNamespace,
    s.Sync.GitLabVisibility,
    HasGitLabToken = s.Sync.IsGitLabConfigured,
    s.Sync.SyncTags,
    s.Sync.CreateMissingRepos,
    s.Sync.IncludeArchived,
    s.Sync.MaxParallel,
    s.Sync.NameMappings,
    s.AutoSyncMinutes,
};

static object Repo(RemoteRepo? r) => r is null ? null! : new { r.FullName, r.WebUrl, r.DefaultBranch, r.IsPrivate, r.Description };

static object PairSummary(RepoPair p) => new
{
    p.Id, p.Name, p.Status, p.IsAnalyzed, p.LastChecked, p.Error,
    GitHub = Repo(p.GitHub), GitLab = Repo(p.GitLab),
    BranchCount = p.Branches.Count, p.DiffCount,
};

static object PairDetail(RepoPair p) => new
{
    p.Id, p.Name, p.Status, p.IsAnalyzed, p.LastChecked, p.Error, p.Tags,
    GitHub = Repo(p.GitHub), GitLab = Repo(p.GitLab),
    BranchCount = p.Branches.Count, p.DiffCount,
    Branches = p.Branches,
};

public sealed record TargetInput(string[]? Ids, string? From);
public sealed record PushInput(string Id, string Branch, string? From, bool Force);

public sealed class SettingsInput
{
    public string? GitHubApiUrl { get; set; }
    public string? GitHubToken { get; set; }
    public string? GitHubOwner { get; set; }
    public string? GitLabUrl { get; set; }
    public string? GitLabToken { get; set; }
    public string? GitLabGroup { get; set; }
    public string? GitLabTargetNamespace { get; set; }
    public string? GitLabVisibility { get; set; }
    public bool? SyncTags { get; set; }
    public bool? CreateMissingRepos { get; set; }
    public bool? IncludeArchived { get; set; }
    public int? MaxParallel { get; set; }
    public string? NameMappings { get; set; }
    public int? AutoSyncMinutes { get; set; }

    /// <summary>Null fields keep the current value; empty tokens keep the stored token.</summary>
    public SyncOptions Apply(SyncOptions cur)
    {
        var o = cur.Clone();
        if (GitHubApiUrl is not null) o.GitHubApiUrl = GitHubApiUrl.Trim();
        if (!string.IsNullOrWhiteSpace(GitHubToken)) o.GitHubToken = GitHubToken.Trim();
        if (GitHubOwner is not null) o.GitHubOwner = GitHubOwner.Trim();
        if (GitLabUrl is not null) o.GitLabUrl = GitLabUrl.Trim();
        if (!string.IsNullOrWhiteSpace(GitLabToken)) o.GitLabToken = GitLabToken.Trim();
        if (GitLabGroup is not null) o.GitLabGroup = GitLabGroup.Trim();
        if (GitLabTargetNamespace is not null) o.GitLabTargetNamespace = GitLabTargetNamespace.Trim();
        if (GitLabVisibility is not null) o.GitLabVisibility = GitLabVisibility;
        if (SyncTags is not null) o.SyncTags = SyncTags.Value;
        if (CreateMissingRepos is not null) o.CreateMissingRepos = CreateMissingRepos.Value;
        if (IncludeArchived is not null) o.IncludeArchived = IncludeArchived.Value;
        if (MaxParallel is not null) o.MaxParallel = Math.Clamp(MaxParallel.Value, 1, 8);
        if (NameMappings is not null) o.NameMappings = NameMappings;
        return o;
    }
}
