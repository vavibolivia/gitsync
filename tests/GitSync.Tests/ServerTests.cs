using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace GitSync.Tests;

/// <summary>Runs the real GitSync.Server process against <see cref="FakeForge"/> and drives it through its HTTP API.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private const string AccessKey = "test-access-key";
    private readonly FakeForge _forge = new();
    private Process? _server;
    private HttpClient _http = null!;
    private string _data = "";
    private string _serverLog = "";

    public async Task InitializeAsync()
    {
        await _forge.StartAsync();
        await SeedAsync();

        var root = FindRepoRoot();
        var dll = Directory.GetFiles(Path.Combine(root, "src", "GitSync.Server", "bin"), "GitSync.Server.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).First();
        var port = FreePort();
        _data = Path.Combine(_forge.Root, "server-data");

        var psi = new ProcessStartInfo("dotnet", $"\"{dll}\"")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        psi.Environment["GITSYNC_DATA"] = _data;
        psi.Environment["GITSYNC_ACCESS_KEY"] = AccessKey;
        psi.Environment["GITSYNC_WEB_ROOT"] = Path.Combine(root, "web");
        psi.Environment["GITSYNC_GITHUB_API_URL"] = _forge.GitHubApiUrl;
        psi.Environment["GITSYNC_GITHUB_TOKEN"] = FakeForge.GitHubToken;
        psi.Environment["GITSYNC_GITLAB_URL"] = _forge.GitLabUrl;
        psi.Environment["GITSYNC_GITLAB_TOKEN"] = FakeForge.GitLabToken;
        psi.Environment["GITSYNC_GITLAB_GROUP"] = "mirrors";
        _server = Process.Start(psi)!;
        _server.OutputDataReceived += (_, e) => _serverLog += e.Data + "\n";
        _server.ErrorDataReceived += (_, e) => _serverLog += e.Data + "\n";
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        _http.DefaultRequestHeaders.Add("X-Access-Key", AccessKey);
        for (int i = 0; i < 100; i++)
        {
            try { if ((await _http.GetAsync("/api/health")).IsSuccessStatusCode) return; } catch { }
            await Task.Delay(200);
        }
        throw new InvalidOperationException("server did not start:\n" + _serverLog);
    }

    public async Task DisposeAsync()
    {
        try { _server?.Kill(true); } catch { }
        _server?.Dispose();
        _http.Dispose();
        await _forge.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        await _forge.AddGitHubAsync("octo/app");
        await _forge.AddGitLabAsync("mirrors/app");
        await _forge.AddGitHubAsync("octo/brand-new");
        var work = Path.Combine(_forge.Root, "seed");
        await FakeForge.Git(null, "init", "-q", "-b", "main", work);
        await FakeForge.Git(work, "config", "user.email", "s@e");
        await FakeForge.Git(work, "config", "user.name", "Seeder");
        async Task Commit(string m) { await File.AppendAllTextAsync(Path.Combine(work, "f"), m); await FakeForge.Git(work, "add", "-A"); await FakeForge.Git(work, "commit", "-q", "-m", m); }
        var gh = _forge.GitHubPath("octo/app");
        var gl = _forge.GitLabPath("mirrors/app");
        await Commit("1"); await FakeForge.Git(work, "push", "-q", gl, "main");
        await Commit("2"); await FakeForge.Git(work, "push", "-q", gh, "main");
        await FakeForge.Git(work, "push", "-q", _forge.GitHubPath("octo/brand-new"), "main");
        await FakeForge.Git(work, "checkout", "-q", "-b", "dev");
        await Commit("d"); await FakeForge.Git(work, "push", "-q", gh, "dev"); await FakeForge.Git(work, "push", "-q", gl, "dev");
        await Commit("dgh"); await FakeForge.Git(work, "push", "-q", gh, "dev");
        await FakeForge.Git(work, "reset", "-q", "--hard", "HEAD~1");
        await Commit("dgl"); await FakeForge.Git(work, "push", "-q", "--force", gl, "dev");
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "GitSync.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task<JsonElement> Get(string path) => await _http.GetFromJsonAsync<JsonElement>(path);

    private async Task<JsonElement> WaitJobAsync()
    {
        for (int i = 0; i < 300; i++)
        {
            var s = await Get("/api/status");
            if (s.TryGetProperty("job", out var job) && !job.GetProperty("running").GetBoolean()) return job;
            await Task.Delay(200);
        }
        throw new TimeoutException("job did not finish\n" + _serverLog);
    }

    private static JsonElement Branch(JsonElement repo, string name) =>
        repo.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("name").GetString() == name);

    /// <summary>The WPF app's "server mode" backend driving the real server.</summary>
    [Fact]
    public async Task Desktop_remote_client_drives_server()
    {
        var log = new GitSync.Core.SyncLogger();
        using (var badKey = new GitSync.Core.RemoteSyncClient(_http.BaseAddress!.ToString(), "wrong", log))
        {
            var e = await Assert.ThrowsAsync<GitSync.Core.SyncException>(() => badKey.GetStatusAsync());
            Assert.Contains("접근 키", e.Message);
        }
        Assert.Throws<GitSync.Core.SyncException>(() => new GitSync.Core.RemoteSyncClient("not a url", "", log));
        using (var down = new GitSync.Core.RemoteSyncClient("http://127.0.0.1:1", AccessKey, log))
            Assert.Contains("연결할 수 없습니다", (await Assert.ThrowsAsync<GitSync.Core.SyncException>(() => down.GetStatusAsync())).Message);

        using GitSync.Core.ISyncBackend client = new GitSync.Core.RemoteSyncClient(_http.BaseAddress!.ToString(), AccessKey, log);
        Assert.True(client.IsRemote);
        Assert.Equal("octo", await client.TestGitHubAsync());
        Assert.Equal("lab", await client.TestGitLabAsync());

        var messages = new List<string>();
        var pairs = await client.ScanAsync(new Progress<string>(m => { lock (messages) messages.Add(m); }));
        Assert.Equal(2, pairs.Count);
        var app = pairs.Single(p => p.Name == "app");
        var fresh = pairs.Single(p => p.Name == "brand-new");
        Assert.Equal("octo/app", app.GitHub!.FullName);
        Assert.Equal("mirrors/app", app.GitLab!.FullName);
        Assert.Equal(GitSync.Core.BranchStatus.Differs, app.Branches.Single(b => b.Name == "main").Status);   // API-compared on the server
        Assert.Null(fresh.GitLab);

        await client.AnalyzeAsync(app);
        Assert.True(app.IsAnalyzed);
        var main = app.Branches.Single(b => b.Name == "main");
        Assert.Equal(GitSync.Core.BranchStatus.GitHubAhead, main.Status);
        Assert.Equal(1, main.GitHubAhead);
        Assert.Equal("Seeder", main.GitHubCommit!.Author);
        Assert.Equal(GitSync.Core.BranchStatus.Diverged, app.Branches.Single(b => b.Name == "dev").Status);
        Assert.Equal(GitSync.Core.BranchActionKind.FastForward,
            GitSync.Core.SyncService.BuildPlan(app, GitSync.Core.Provider.GitHub).Actions.Single(a => a.Branch == "main").Kind);

        // Bulk sync as ONE server job (creates the missing GitLab project).
        var results = await client.SyncManyAsync(pairs, GitSync.Core.Provider.GitHub);
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.True(results.Single(r => r.Repo == "brand-new").CreatedRepo);
        Assert.Equal(1, results.Single(r => r.Repo == "app").Pushed);
        Assert.Equal("mirrors/brand-new", fresh.GitLab!.FullName);
        Assert.Equal(GitSync.Core.RepoStatus.Synced, fresh.Status);
        Assert.Equal(GitSync.Core.BranchStatus.Diverged, app.Branches.Single(b => b.Name == "dev").Status);

        // Push without force is refused by the server, with force it succeeds.
        var noForce = await Assert.ThrowsAsync<GitSync.Core.SyncException>(() =>
            client.PushBranchAsync(app, "dev", GitSync.Core.Provider.GitHub, force: false));
        Assert.Contains("강제", noForce.Message);
        await client.PushBranchAsync(app, "dev", GitSync.Core.Provider.GitHub, force: true);
        Assert.Equal(GitSync.Core.RepoStatus.Synced, app.Status);
        Assert.Equal((await FakeForge.RefsAsync(_forge.GitHubPath("octo/app")))["refs/heads/dev"],
                     (await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/app")))["refs/heads/dev"]);

        // Single-repo sync when nothing is left to do.
        var noop = await client.SyncAsync(app, GitSync.Core.Provider.GitHub);
        Assert.True(noop.Ok, noop.Error);
        Assert.Equal(0, noop.Pushed);

        // Settings round trip through the client (tokens write-only).
        var remote = (GitSync.Core.RemoteSyncClient)client;
        var saved = await remote.SaveSettingsAsync(new { maxParallel = 4, autoSyncMinutes = 30, gitHubToken = (string?)null });
        Assert.Equal(4, saved.MaxParallel);
        Assert.Equal(30, saved.AutoSyncMinutes);
        Assert.True(saved.HasGitHubToken);
        Assert.Equal(4, client.MaxParallel);

        // Server logs are mirrored into the local logger.
        Assert.Contains(log.Since(0), e => e.Message.Contains("일괄 동기화 완료"));
        Assert.Contains(log.Since(0), e => e.Message.Contains("강제 푸시 완료"));
        Assert.DoesNotContain(log.Since(0), e => (e.Message + e.Detail).Contains(FakeForge.GitHubToken));
        await Task.Delay(200);   // Progress<T> posts asynchronously
        lock (messages) Assert.Contains(messages, m => m.Contains("작업 시작"));
    }

    [Fact]
    public async Task Full_flow_through_http_api()
    {
        // ── access control ──
        using (var anon = new HttpClient { BaseAddress = _http.BaseAddress })
        {
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/status")).StatusCode);
            anon.DefaultRequestHeaders.Add("X-Access-Key", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/repos")).StatusCode);
            var page = await anon.GetStringAsync("/");                                  // web UI is public, API is not
            Assert.Contains("<title>GitSync</title>", page);
        }

        // ── configured from environment ──
        var status = await Get("/api/status");
        Assert.True(status.GetProperty("configured").GetBoolean());

        // ── connection test ──
        var test = await (await _http.PostAsJsonAsync("/api/test/github", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(test.GetProperty("ok").GetBoolean());
        Assert.Equal("octo", test.GetProperty("user").GetString());
        var badTest = await (await _http.PostAsJsonAsync("/api/test/gitlab", new { gitLabToken = "nope" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(badTest.GetProperty("ok").GetBoolean());
        Assert.Equal(["gl-api /gl/api/v4/user"], _forge.AuthFailures.ToArray());   // only the deliberate bad-token test
        _forge.AuthFailures.Clear();

        // ── scan ──
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync("/api/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync("/api/scan", null)).StatusCode);   // one job at a time
        var job = await WaitJobAsync();
        Assert.False(job.TryGetProperty("error", out _), job.ToString());
        status = await Get("/api/status");
        Assert.Equal("octo", status.GetProperty("githubUser").GetString());
        Assert.Equal("lab", status.GetProperty("gitlabUser").GetString());
        Assert.Equal(2, status.GetProperty("counts").GetProperty("total").GetInt32());
        Assert.Equal(1, status.GetProperty("counts").GetProperty("githubOnly").GetInt32());

        var repos = await Get("/api/repos");
        Assert.Equal(2, repos.GetArrayLength());
        var app = await Get("/api/repo?id=" + Uri.EscapeDataString("gh_octo/app"));
        Assert.Equal("Differs", Branch(app, "main").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/repo?id=nope")).StatusCode);

        // ── sync everything to GitLab ──
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsJsonAsync("/api/sync", new { from = "github" })).StatusCode);
        job = await WaitJobAsync();
        Assert.False(job.TryGetProperty("error", out _), job.ToString());

        app = await Get("/api/repo?id=" + Uri.EscapeDataString("gh_octo/app"));
        Assert.Equal("Synced", Branch(app, "main").GetProperty("status").GetString());
        Assert.Equal("Diverged", Branch(app, "dev").GetProperty("status").GetString());
        var created = await Get("/api/repo?id=" + Uri.EscapeDataString("gh_octo/brand-new"));
        Assert.Equal("mirrors/brand-new", created.GetProperty("gitLab").GetProperty("fullName").GetString());
        Assert.Equal("Synced", created.GetProperty("status").GetString());
        Assert.Equal((await FakeForge.RefsAsync(_forge.GitHubPath("octo/brand-new")))["refs/heads/main"],
                     (await FakeForge.RefsAsync(_forge.GitLabPath("mirrors/brand-new")))["refs/heads/main"]);

        // ── diverged branch requires explicit force ──
        await _http.PostAsJsonAsync("/api/push", new { id = "gh_octo/app", branch = "dev", from = "github", force = false });
        job = await WaitJobAsync();
        Assert.Contains("강제", job.GetProperty("error").GetString());
        await _http.PostAsJsonAsync("/api/push", new { id = "gh_octo/app", branch = "dev", from = "github", force = true });
        job = await WaitJobAsync();
        Assert.False(job.TryGetProperty("error", out _), job.ToString());
        app = await Get("/api/repo?id=" + Uri.EscapeDataString("gh_octo/app"));
        Assert.Equal("Synced", app.GetProperty("status").GetString());

        // ── analyze ──
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsJsonAsync("/api/analyze", new { ids = new[] { "gh_octo/app" } })).StatusCode);
        job = await WaitJobAsync();
        Assert.False(job.TryGetProperty("error", out _), job.ToString());

        // ── settings: tokens are write-only and blank input keeps them ──
        var settings = await _http.GetStringAsync("/api/settings");
        Assert.DoesNotContain(FakeForge.GitHubToken, settings);
        Assert.Contains("\"hasGitHubToken\":true", settings);
        var put = await _http.PutAsJsonAsync("/api/settings", new { gitHubToken = "", maxParallel = 2, autoSyncMinutes = 15, nameMappings = "a=b" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var s2 = await Get("/api/settings");
        Assert.True(s2.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal(2, s2.GetProperty("maxParallel").GetInt32());
        Assert.Equal(15, s2.GetProperty("autoSyncMinutes").GetInt32());
        Assert.Equal("a=b", s2.GetProperty("nameMappings").GetString());
        Assert.True(File.Exists(Path.Combine(_data, "settings.json")));
        Assert.Equal(15, (await Get("/api/status")).GetProperty("autoSyncMinutes").GetInt32());

        // ── logs: present, and never contain tokens ──
        var logs = await _http.GetStringAsync("/api/logs?after=0");
        Assert.Contains("동기화", logs);
        Assert.DoesNotContain(FakeForge.GitHubToken, logs);
        Assert.DoesNotContain(FakeForge.GitLabToken, logs);
        var after = await Get("/api/logs?after=999999");
        Assert.Equal(0, after.GetArrayLength());
        Assert.Empty(_forge.AuthFailures);
    }
}
