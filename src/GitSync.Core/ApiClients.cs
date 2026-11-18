using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GitSync.Core;

public abstract class ApiClientBase : IDisposable
{
    protected readonly HttpClient Http;
    private readonly string _label;

    protected ApiClientBase(string baseUrl, string label)
    {
        _label = label;
        Http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(90) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("GitSync/1.0");
    }

    protected async Task<JsonDocument?> SendAsync(HttpMethod method, string path, object? body,
        CancellationToken ct, bool allowNotFound = false)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException($"{_label} 서버에 연결할 수 없습니다: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException($"{_label} API 응답 시간이 초과되었습니다.");
        }

        using (resp)
        {
            if (allowNotFound && resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                return null;

            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = ExtractMessage(text);
                var hint = resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => " (토큰이 올바르지 않거나 만료되었습니다)",
                    HttpStatusCode.Forbidden => " (권한 부족 또는 API 사용량 제한)",
                    _ => "",
                };
                throw new SyncException($"{_label} API {(int)resp.StatusCode} {method} {path}: {msg}{hint}", text);
            }
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }
    }

    private static string ExtractMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            foreach (var key in new[] { "message", "error_description", "error" })
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var m))
                    return m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : m.ToString();
        }
        catch (JsonException) { }
        return text.Length > 200 ? text[..200] : text;
    }

    protected static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    protected static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    protected static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;

    public void Dispose() => Http.Dispose();
}

public sealed class GitHubClient : ApiClientBase
{
    private string? _login;

    public GitHubClient(string apiUrl, string token)
        : base((string.IsNullOrWhiteSpace(apiUrl) ? "https://api.github.com" : apiUrl.Trim()).TrimEnd('/') + "/", "GitHub")
    {
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        Http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<string> GetLoginAsync(CancellationToken ct = default)
    {
        if (_login is not null) return _login;
        using var doc = await SendAsync(HttpMethod.Get, "user", null, ct);
        return _login = Str(doc!.RootElement, "login") ?? "";
    }

    public async Task<List<RemoteRepo>> GetReposAsync(string? owner, bool includeArchived, CancellationToken ct = default)
    {
        string basePath;
        var login = await GetLoginAsync(ct);
        if (string.IsNullOrWhiteSpace(owner) || owner.Trim().Equals(login, StringComparison.OrdinalIgnoreCase))
        {
            basePath = "user/repos?affiliation=owner&sort=full_name";
        }
        else
        {
            owner = owner.Trim();
            using var org = await SendAsync(HttpMethod.Get, $"orgs/{Uri.EscapeDataString(owner)}", null, ct, allowNotFound: true);
            basePath = org is not null
                ? $"orgs/{Uri.EscapeDataString(owner)}/repos?type=all&sort=full_name"
                : $"users/{Uri.EscapeDataString(owner)}/repos?type=owner&sort=full_name";
        }

        var list = new List<RemoteRepo>();
        for (int page = 1; ; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"{basePath}&per_page=100&page={page}", null, ct);
            int count = 0;
            foreach (var e in doc!.RootElement.EnumerateArray())
            {
                count++;
                if (!includeArchived && Bool(e, "archived")) continue;
                list.Add(Map(e));
            }
            if (count < 100) break;
        }
        return list;
    }

    public async Task<List<RemoteBranch>> GetBranchesAsync(string fullName, CancellationToken ct = default)
    {
        var list = new List<RemoteBranch>();
        for (int page = 1; ; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"repos/{fullName}/branches?per_page=100&page={page}", null, ct, allowNotFound: true);
            if (doc is null) break;
            int count = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                count++;
                var sha = e.TryGetProperty("commit", out var c) ? Str(c, "sha") : null;
                list.Add(new RemoteBranch(Str(e, "name") ?? "", sha ?? ""));
            }
            if (count < 100) break;
        }
        return list;
    }

    public async Task<RemoteRepo> CreateRepoAsync(string name, string? owner, bool isPrivate, string? description, CancellationToken ct = default)
    {
        var login = await GetLoginAsync(ct);
        var path = string.IsNullOrWhiteSpace(owner) || owner.Trim().Equals(login, StringComparison.OrdinalIgnoreCase)
            ? "user/repos"
            : $"orgs/{Uri.EscapeDataString(owner.Trim())}/repos";
        using var doc = await SendAsync(HttpMethod.Post, path,
            new { name, @private = isPrivate, description = description ?? "", auto_init = false }, ct);
        return Map(doc!.RootElement);
    }

    public async Task SetDefaultBranchAsync(string fullName, string branch, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Patch, $"repos/{fullName}", new { default_branch = branch }, ct);
    }

    private static RemoteRepo Map(JsonElement e) => new(
        Provider.GitHub,
        Long(e, "id"),
        Str(e, "name") ?? "",
        Str(e, "full_name") ?? "",
        Str(e, "html_url") ?? "",
        Str(e, "clone_url") ?? "",
        Str(e, "default_branch"),
        Date(e, "pushed_at") ?? Date(e, "updated_at"),
        Bool(e, "private"),
        Str(e, "description"));
}

public sealed class GitLabClient : ApiClientBase
{
    private string? _username;

    public GitLabClient(string serverUrl, string token)
        : base((string.IsNullOrWhiteSpace(serverUrl) ? "https://gitlab.com" : serverUrl.Trim()).TrimEnd('/') + "/api/v4/", "GitLab")
    {
        Http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token.Trim());
    }

    public async Task<string> GetUsernameAsync(CancellationToken ct = default)
    {
        if (_username is not null) return _username;
        using var doc = await SendAsync(HttpMethod.Get, "user", null, ct);
        return _username = Str(doc!.RootElement, "username") ?? "";
    }

    public async Task<List<RemoteRepo>> GetProjectsAsync(string? group, bool includeArchived, CancellationToken ct = default)
    {
        var archived = includeArchived ? "" : "&archived=false";
        var basePath = string.IsNullOrWhiteSpace(group)
            ? $"projects?membership=true&order_by=path&sort=asc{archived}"
            : $"groups/{Uri.EscapeDataString(group.Trim())}/projects?include_subgroups=true&order_by=path&sort=asc{archived}";

        var list = new List<RemoteRepo>();
        for (int page = 1; ; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"{basePath}&per_page=100&page={page}", null, ct);
            int count = 0;
            foreach (var e in doc!.RootElement.EnumerateArray())
            {
                count++;
                list.Add(Map(e));
            }
            if (count < 100) break;
        }
        return list;
    }

    public async Task<List<RemoteBranch>> GetBranchesAsync(long projectId, CancellationToken ct = default)
    {
        var list = new List<RemoteBranch>();
        for (int page = 1; ; page++)
        {
            using var doc = await SendAsync(HttpMethod.Get, $"projects/{projectId}/repository/branches?per_page=100&page={page}", null, ct, allowNotFound: true);
            if (doc is null) break;
            int count = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                count++;
                var sha = e.TryGetProperty("commit", out var c) ? Str(c, "id") : null;
                list.Add(new RemoteBranch(Str(e, "name") ?? "", sha ?? ""));
            }
            if (count < 100) break;
        }
        return list;
    }

    public async Task<RemoteRepo> CreateProjectAsync(string name, string? @namespace, string visibility, string? description, CancellationToken ct = default)
    {
        long? namespaceId = null;
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            using var ns = await SendAsync(HttpMethod.Get, $"namespaces/{Uri.EscapeDataString(@namespace.Trim())}", null, ct, allowNotFound: true)
                ?? throw new SyncException($"GitLab 네임스페이스 '{@namespace}'를 찾을 수 없습니다.");
            namespaceId = Long(ns.RootElement, "id");
        }

        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["path"] = name,
            ["visibility"] = visibility,
            ["description"] = description ?? "",
            ["initialize_with_readme"] = false,
        };
        if (namespaceId is not null) body["namespace_id"] = namespaceId;

        using var doc = await SendAsync(HttpMethod.Post, "projects", body, ct);
        return Map(doc!.RootElement);
    }

    public async Task SetDefaultBranchAsync(long projectId, string branch, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Put, $"projects/{projectId}", new { default_branch = branch }, ct);
    }

    private static RemoteRepo Map(JsonElement e) => new(
        Provider.GitLab,
        Long(e, "id"),
        Str(e, "path") ?? "",
        Str(e, "path_with_namespace") ?? "",
        Str(e, "web_url") ?? "",
        Str(e, "http_url_to_repo") ?? "",
        Str(e, "default_branch"),
        Date(e, "last_activity_at"),
        Str(e, "visibility") != "public",
        Str(e, "description"));
}
