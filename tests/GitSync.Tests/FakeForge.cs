using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GitSync.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitSync.Tests;

/// <summary>
/// An in-process fake of the GitHub REST API, the GitLab REST API and both git smart-HTTP endpoints.
/// Repositories are real bare git repositories served through `git http-backend`, so the engine is
/// exercised exactly as against the real services (HTTP auth headers, fetch, push, pagination, creation).
/// </summary>
public sealed class FakeForge : IAsyncDisposable
{
    public const string GitHubToken = "ghp_test_token_123";
    public const string GitLabToken = "glpat_test_token_456";
    public const string GitHubLogin = "octo";
    public const string GitLabUser = "lab";

    private WebApplication? _app;
    private long _nextGlId = 1000;

    public string Root { get; }
    public string BaseUrl { get; private set; } = "";
    public string GitHubApiUrl => BaseUrl + "/gh/api";
    public string GitLabUrl => BaseUrl + "/gl";

    public sealed class Repo
    {
        public required string FullName { get; init; }       // owner/name or group/sub/name
        public long Id { get; init; }
        public bool Private { get; set; } = true;
        public bool Archived { get; set; }
        public string? Description { get; set; }
        public bool Materialized { get; set; }
        public string Name => FullName[(FullName.LastIndexOf('/') + 1)..];
        public string Namespace => FullName[..FullName.LastIndexOf('/')];
    }

    public ConcurrentDictionary<string, Repo> GitHubRepos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Repo> GitLabRepos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> GitHubOrgs { get; } = new(StringComparer.OrdinalIgnoreCase) { "acme" };
    public Dictionary<string, long> GitLabNamespaces { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mirrors"] = 11, ["mirrors/sub"] = 12, ["other"] = 13, [GitLabUser] = 14,
    };
    public ConcurrentQueue<string> RequestLog { get; } = new();
    public ConcurrentQueue<string> AuthFailures { get; } = new();

    public FakeForge()
    {
        Root = Path.Combine(Path.GetTempPath(), "gitsync-forge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Root, "gh"));
        Directory.CreateDirectory(Path.Combine(Root, "gl"));
    }

    public string GitHubPath(string fullName) => Path.Combine(Root, "gh", fullName.Replace('/', Path.DirectorySeparatorChar) + ".git");
    public string GitLabPath(string fullName) => Path.Combine(Root, "gl", fullName.Replace('/', Path.DirectorySeparatorChar) + ".git");

    // ───────────────────────── setup helpers ─────────────────────────

    public async Task<Repo> AddGitHubAsync(string fullName, bool materialize = true, bool isPrivate = true, bool archived = false)
    {
        var r = new Repo { FullName = fullName, Id = GitHubRepos.Count + 1, Private = isPrivate, Archived = archived, Description = "desc " + fullName };
        GitHubRepos[fullName] = r;
        if (materialize) await InitBareAsync(GitHubPath(fullName), r);
        return r;
    }

    public async Task<Repo> AddGitLabAsync(string fullName, bool materialize = true)
    {
        var r = new Repo { FullName = fullName, Id = Interlocked.Increment(ref _nextGlId) };
        GitLabRepos[fullName] = r;
        if (materialize) await InitBareAsync(GitLabPath(fullName), r);
        return r;
    }

    private static async Task InitBareAsync(string path, Repo r)
    {
        Directory.CreateDirectory(path);
        await Git(null, "init", "--bare", "-q", "-b", "main", path);
        await Git(path, "config", "http.receivepack", "true");
        r.Materialized = true;
    }

    public static async Task<string> Git(string? dir, params string[] args)
    {
        var r = await GitCli.RunAsync(dir, args);
        if (!r.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)} failed:\n{r.StdErr}");
        return r.StdOut.Trim();
    }

    /// <summary>Returns refname → sha for a bare repository.</summary>
    public static async Task<Dictionary<string, string>> RefsAsync(string bare)
    {
        var map = new Dictionary<string, string>();
        var outp = await Git(bare, "for-each-ref", "--format=%(refname) %(objectname)");
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.LastIndexOf(' ');
            map[line[..i]] = line[(i + 1)..];
        }
        return map;
    }

    // ───────────────────────── server ─────────────────────────

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            RequestLog.Enqueue($"{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString}");
            await next();
        });
        MapGitHub(_app);
        MapGitLab(_app);
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private static IResult Json(object o, int status = 200) => Results.Json(o, statusCode: status);

    private bool GhAuthorized(HttpContext ctx)
    {
        var ok = ctx.Request.Headers.Authorization.ToString() == "Bearer " + GitHubToken;
        if (!ok) AuthFailures.Enqueue("gh-api " + ctx.Request.Path);
        return ok;
    }

    private bool GlAuthorized(HttpContext ctx)
    {
        var ok = ctx.Request.Headers["PRIVATE-TOKEN"].ToString() == GitLabToken;
        if (!ok) AuthFailures.Enqueue("gl-api " + ctx.Request.Path);
        return ok;
    }

    private static (int page, int per) Paging(HttpContext ctx) =>
        (int.TryParse(ctx.Request.Query["page"], out var p) ? p : 1, int.TryParse(ctx.Request.Query["per_page"], out var n) ? n : 30);

    private static IEnumerable<T> Page<T>(IEnumerable<T> items, HttpContext ctx)
    {
        var (page, per) = Paging(ctx);
        return items.Skip((page - 1) * per).Take(per);
    }

    private object GhRepoJson(Repo r) => new
    {
        id = r.Id, name = r.Name, full_name = r.FullName, html_url = $"https://github.example/{r.FullName}",
        clone_url = $"{BaseUrl}/gh/git/{r.FullName}.git", default_branch = "main", pushed_at = "2026-11-01T10:00:00Z",
        @private = r.Private, archived = r.Archived, description = r.Description,
    };

    private object GlRepoJson(Repo r) => new
    {
        id = r.Id, name = r.Name, path = r.Name, path_with_namespace = r.FullName, web_url = $"https://gitlab.example/{r.FullName}",
        http_url_to_repo = $"{BaseUrl}/gl/git/{r.FullName}.git", default_branch = "main", last_activity_at = "2026-11-01T10:00:00Z",
        visibility = r.Private ? "private" : "public", description = r.Description, archived = r.Archived,
    };

    private static async Task<List<object>> BranchesAsync(string bare, Func<string, string, object> map)
    {
        var list = new List<object>();
        if (!Directory.Exists(bare)) return list;
        var outp = await Git(bare, "for-each-ref", "--format=%(refname:strip=2) %(objectname)", "refs/heads/");
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = line.LastIndexOf(' ');
            list.Add(map(line[..i], line[(i + 1)..]));
        }
        return list;
    }

    /// <summary>Tag name and the commit it points at (annotated tags are peeled), like the real tag APIs.</summary>
    private static async Task<List<object>> TagsAsync(string bare, Func<string, string, object> map)
    {
        var list = new List<object>();
        if (!Directory.Exists(bare)) return list;
        var outp = await Git(bare, "for-each-ref", "--format=%(refname:strip=2)|%(objectname)|%(*objectname)", "refs/tags/");
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Trim().Split('|');
            list.Add(map(f[0], f.Length > 2 && f[2].Length > 0 ? f[2] : f[1]));
        }
        return list;
    }

    private void MapGitHub(WebApplication app)
    {
        var api = app.MapGroup("/gh/api");
        api.AddEndpointFilter(async (c, next) => GhAuthorized(c.HttpContext) ? await next(c) : Json(new { message = "Bad credentials" }, 401));

        api.MapGet("/user", () => Json(new { login = GitHubLogin }));
        api.MapGet("/orgs/{org}", (string org) => GitHubOrgs.Contains(org) ? Json(new { login = org }) : Json(new { message = "Not Found" }, 404));
        api.MapGet("/user/repos", (HttpContext ctx) =>
            Json(Page(GitHubRepos.Values.Where(r => r.Namespace == GitHubLogin).OrderBy(r => r.FullName).Select(GhRepoJson), ctx)));
        api.MapGet("/orgs/{org}/repos", (string org, HttpContext ctx) =>
            Json(Page(GitHubRepos.Values.Where(r => r.Namespace.Equals(org, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.FullName).Select(GhRepoJson), ctx)));
        api.MapGet("/users/{user}/repos", (string user, HttpContext ctx) =>
            Json(Page(GitHubRepos.Values.Where(r => r.Namespace.Equals(user, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.FullName).Select(GhRepoJson), ctx)));
        api.MapGet("/repos/{owner}/{repo}/branches", async (string owner, string repo, HttpContext ctx) =>
        {
            if (!GitHubRepos.ContainsKey($"{owner}/{repo}")) return Json(new { message = "Not Found" }, 404);
            var all = await BranchesAsync(GitHubPath($"{owner}/{repo}"), (n, s) => new { name = n, commit = new { sha = s } });
            return Json(Page(all, ctx));
        });

        api.MapGet("/repos/{owner}/{repo}/tags", async (string owner, string repo, HttpContext ctx) =>
        {
            if (!GitHubRepos.ContainsKey($"{owner}/{repo}")) return Json(new { message = "Not Found" }, 404);
            var all = await TagsAsync(GitHubPath($"{owner}/{repo}"), (n, s) => new { name = n, commit = new { sha = s } });
            return Json(Page(all, ctx));
        });

        async Task<IResult> Create(string owner, HttpContext ctx)
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var name = doc.RootElement.GetProperty("name").GetString()!;
            var full = $"{owner}/{name}";
            if (GitHubRepos.ContainsKey(full)) return Json(new { message = "name already exists on this account" }, 422);
            var r = await AddGitHubAsync(full, isPrivate: doc.RootElement.GetProperty("private").GetBoolean());
            return Json(GhRepoJson(r), 201);
        }
        api.MapPost("/user/repos", (Delegate)((HttpContext ctx) => Create(GitHubLogin, ctx)));
        api.MapPost("/orgs/{org}/repos", (string org, HttpContext ctx) => Create(org, ctx));
        api.MapMethods("/repos/{owner}/{repo}", ["PATCH"], async (string owner, string repo, HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var branch = doc.RootElement.GetProperty("default_branch").GetString()!;
            await Git(GitHubPath($"{owner}/{repo}"), "symbolic-ref", "HEAD", "refs/heads/" + branch);
            return Json(GhRepoJson(GitHubRepos[$"{owner}/{repo}"]));
        });

        app.Map("/gh/git/{**path}", (HttpContext ctx, string path) =>
            GitBackend(ctx, Path.Combine(Root, "gh"), path, "x-access-token", GitHubToken));
    }

    private void MapGitLab(WebApplication app)
    {
        var api = app.MapGroup("/gl/api/v4");
        api.AddEndpointFilter(async (c, next) => GlAuthorized(c.HttpContext) ? await next(c) : Json(new { message = "401 Unauthorized" }, 401));

        api.MapGet("/user", () => Json(new { username = GitLabUser }));
        api.MapGet("/projects", (HttpContext ctx) =>
        {
            var archivedFilter = ctx.Request.Query["archived"].ToString();
            var items = GitLabRepos.Values.Where(r => archivedFilter != "false" || !r.Archived).OrderBy(r => r.FullName);
            return Json(Page(items.Select(GlRepoJson), ctx));
        });
        api.MapGet("/groups/{group}/projects", (string group, HttpContext ctx) =>
        {
            group = Uri.UnescapeDataString(group);
            var items = GitLabRepos.Values.Where(r => r.Namespace.Equals(group, StringComparison.OrdinalIgnoreCase)
                                                      || r.Namespace.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.FullName);
            return Json(Page(items.Select(GlRepoJson), ctx));
        });
        api.MapGet("/namespaces/{ns}", (string ns) =>
            GitLabNamespaces.TryGetValue(Uri.UnescapeDataString(ns), out var id) ? Json(new { id, full_path = ns }) : Json(new { message = "404 Not found" }, 404));
        api.MapGet("/projects/{id:long}/repository/branches", async (long id, HttpContext ctx) =>
        {
            var r = GitLabRepos.Values.FirstOrDefault(x => x.Id == id);
            if (r is null) return Json(new { message = "404 Project Not Found" }, 404);
            var all = await BranchesAsync(GitLabPath(r.FullName), (n, s) => new { name = n, commit = new { id = s } });
            return Json(Page(all, ctx));
        });
        api.MapGet("/projects/{id:long}/repository/tags", async (long id, HttpContext ctx) =>
        {
            var r = GitLabRepos.Values.FirstOrDefault(x => x.Id == id);
            if (r is null) return Json(new { message = "404 Project Not Found" }, 404);
            var all = await TagsAsync(GitLabPath(r.FullName), (n, s) => new { name = n, commit = new { id = s } });
            return Json(Page(all, ctx));
        });
        api.MapPost("/projects", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var root = doc.RootElement;
            var path = root.GetProperty("path").GetString()!;
            var ns = GitLabUser;
            if (root.TryGetProperty("namespace_id", out var nsId))
                ns = GitLabNamespaces.First(kv => kv.Value == nsId.GetInt64()).Key;
            var full = $"{ns}/{path}";
            if (GitLabRepos.ContainsKey(full)) return Json(new { message = new { name = new[] { "has already been taken" } } }, 400);
            var r = await AddGitLabAsync(full);
            r.Private = root.GetProperty("visibility").GetString() != "public";
            r.Description = root.GetProperty("description").GetString();
            CreatedVisibility[full] = root.GetProperty("visibility").GetString()!;
            return Json(GlRepoJson(r), 201);
        });
        api.MapPut("/projects/{id:long}", async (long id, HttpContext ctx) =>
        {
            var r = GitLabRepos.Values.First(x => x.Id == id);
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var branch = doc.RootElement.GetProperty("default_branch").GetString()!;
            await Git(GitLabPath(r.FullName), "symbolic-ref", "HEAD", "refs/heads/" + branch);
            return Json(GlRepoJson(r));
        });

        app.Map("/gl/git/{**path}", (HttpContext ctx, string path) =>
            GitBackend(ctx, Path.Combine(Root, "gl"), path, "oauth2", GitLabToken));
    }

    public ConcurrentDictionary<string, string> CreatedVisibility { get; } = new();

    // ───────────────────────── git smart HTTP via git http-backend (CGI) ─────────────────────────

    private async Task GitBackend(HttpContext ctx, string projectRoot, string path, string user, string token)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
        if (ctx.Request.Headers.Authorization.ToString() != expected)
        {
            AuthFailures.Enqueue("git " + path);
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"fake\"";
            return;
        }

        var psi = new ProcessStartInfo("git", "http-backend")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.Environment["GIT_PROJECT_ROOT"] = projectRoot.Replace('\\', '/');
        psi.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
        psi.Environment["PATH_INFO"] = "/" + path;
        psi.Environment["REQUEST_METHOD"] = ctx.Request.Method;
        psi.Environment["QUERY_STRING"] = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value![1..] : "";
        psi.Environment["CONTENT_TYPE"] = ctx.Request.ContentType ?? "";
        psi.Environment["REMOTE_USER"] = user;
        psi.Environment["REMOTE_ADDR"] = "127.0.0.1";
        psi.Environment["GIT_HTTP_MAX_REQUEST_BUFFER"] = "100M";
        if (ctx.Request.Headers.ContentEncoding.Count > 0) psi.Environment["HTTP_CONTENT_ENCODING"] = ctx.Request.Headers.ContentEncoding.ToString();
        if (ctx.Request.Headers.TryGetValue("Git-Protocol", out var gp)) psi.Environment["GIT_PROTOCOL"] = gp.ToString();
        if (ctx.Request.ContentLength is { } len) psi.Environment["CONTENT_LENGTH"] = len.ToString();

        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var copyIn = Task.Run(async () =>
        {
            await ctx.Request.Body.CopyToAsync(p.StandardInput.BaseStream);
            p.StandardInput.Close();
        });

        var stdout = p.StandardOutput.BaseStream;
        var header = new MemoryStream();
        // Read CGI headers byte by byte until a blank line.
        int prev = -1, cur, newlines = 0;
        while ((cur = stdout.ReadByte()) != -1)
        {
            header.WriteByte((byte)cur);
            if (cur == '\n') { newlines++; if (newlines == 2) break; }
            else if (cur != '\r') newlines = 0;
            prev = cur;
        }
        foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            var name = line[..i].Trim();
            var value = line[(i + 1)..].Trim();
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase)) ctx.Response.StatusCode = int.Parse(value[..3]);
            else ctx.Response.Headers[name] = value;
        }
        await stdout.CopyToAsync(ctx.Response.Body);
        await copyIn;
        await p.WaitForExitAsync();
        var err = await errTask;
        if (!string.IsNullOrWhiteSpace(err)) RequestLog.Enqueue("http-backend stderr: " + err.Trim());
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { DeleteDirectory(Root); } catch { /* best effort */ }
    }

    public static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }
}
