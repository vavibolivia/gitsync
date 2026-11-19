namespace GitSync.Core;

public sealed class SyncOptions
{
    public string GitHubApiUrl { get; set; } = "https://api.github.com";
    public string GitHubToken { get; set; } = "";
    /// <summary>Optional user or organization. Empty = every repository the token owns.</summary>
    public string GitHubOwner { get; set; } = "";

    public string GitLabUrl { get; set; } = "https://gitlab.com";
    public string GitLabToken { get; set; } = "";
    /// <summary>Optional group (full path) used to scope the GitLab scan. Empty = all projects you are a member of.</summary>
    public string GitLabGroup { get; set; } = "";
    /// <summary>Namespace (group path) in which missing projects are created. Empty = GitLabGroup, or your user namespace.</summary>
    public string GitLabTargetNamespace { get; set; } = "";
    /// <summary>private | internal | public | source</summary>
    public string GitLabVisibility { get; set; } = "private";

    public string CacheRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitSync", "cache");
    public bool SyncTags { get; set; } = true;
    public bool CreateMissingRepos { get; set; } = true;
    public bool IncludeArchived { get; set; }
    public int MaxParallel { get; set; } = 3;
    /// <summary>One mapping per line: githubName=gitlabPath</summary>
    public string NameMappings { get; set; } = "";

    /// <summary>Optional GitLab user name. When set, the key must belong to this account.</summary>
    public string GitLabUser { get; set; } = "";
    /// <summary>When true, only the pairs listed in <see cref="Pairs"/> are shown instead of every repository.</summary>
    public bool ManualPairs { get; set; }
    /// <summary>One pair per line: githubRepo=gitlabProject. Either side may be empty (it is created on sync).</summary>
    public string Pairs { get; set; } = "";

    public List<(string GitHub, string GitLab)> ParsePairs()
    {
        var list = new List<(string, string)>();
        foreach (var raw in (Pairs ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf('=');
            if (line.Length == 0 || line.StartsWith('#') || idx < 0) continue;
            var gh = line[..idx].Trim();
            var gl = line[(idx + 1)..].Trim();
            if (gh.Length > 0 || gl.Length > 0) list.Add((gh, gl));
        }
        return list;
    }

    /// <summary>
    /// Accepts what people actually type for GitHub: nothing or "github.com" (→ api.github.com), a full API URL,
    /// or just the host/IP of a GitHub Enterprise server (→ https://host/api/v3).
    /// </summary>
    public static string NormalizeGitHubApiUrl(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "https://api.github.com";
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return s;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return "https://api.github.com";
        return uri.AbsolutePath.Trim('/').Length == 0 && !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            ? s + "/api/v3"
            : s;
    }

    /// <summary>Accepts a full URL or just the host/IP of a GitLab server (https is assumed when no scheme is given).</summary>
    public static string NormalizeGitLabUrl(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "https://gitlab.com";
        return s.Contains("://") ? s : "https://" + s;
    }

    public bool IsGitHubConfigured => !string.IsNullOrWhiteSpace(GitHubToken);
    public bool IsGitLabConfigured => !string.IsNullOrWhiteSpace(GitLabToken);

    public SyncOptions Clone() => (SyncOptions)MemberwiseClone();

    public string EffectiveTargetNamespace =>
        !string.IsNullOrWhiteSpace(GitLabTargetNamespace) ? GitLabTargetNamespace.Trim() : GitLabGroup.Trim();

    public Dictionary<string, string> ParseMappings()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (NameMappings ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var gh = line[..idx].Trim();
            var gl = line[(idx + 1)..].Trim();
            if (gh.Length > 0 && gl.Length > 0) map[gh] = gl;
        }
        return map;
    }
}
