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
