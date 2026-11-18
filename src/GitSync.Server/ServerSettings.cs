using System.Text.Json;
using GitSync.Core;

namespace GitSync.Server;

public sealed class ServerSettings
{
    public SyncOptions Sync { get; set; } = new();
    /// <summary>0 = off. Otherwise: scan + GitHub → GitLab sync every N minutes.</summary>
    public int AutoSyncMinutes { get; set; }
}

/// <summary>
/// Settings live in $GITSYNC_DATA/settings.json (default /data). Environment variables override the file:
/// GITSYNC_GITHUB_TOKEN, GITSYNC_GITHUB_API_URL, GITSYNC_GITHUB_OWNER,
/// GITSYNC_GITLAB_TOKEN, GITSYNC_GITLAB_URL, GITSYNC_GITLAB_GROUP, GITSYNC_GITLAB_NAMESPACE, GITSYNC_AUTO_SYNC_MINUTES
/// </summary>
public sealed class SettingsFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string DataDir { get; }
    private string FilePath => Path.Combine(DataDir, "settings.json");

    public SettingsFile(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);
    }

    public ServerSettings Load()
    {
        lock (_gate)
        {
            ServerSettings s;
            try
            {
                s = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(FilePath), Json) ?? new()
                    : new();
            }
            catch { s = new(); }

            s.Sync.CacheRoot = Path.Combine(DataDir, "cache");
            Env("GITSYNC_GITHUB_TOKEN", v => s.Sync.GitHubToken = v);
            Env("GITSYNC_GITHUB_API_URL", v => s.Sync.GitHubApiUrl = v);
            Env("GITSYNC_GITHUB_OWNER", v => s.Sync.GitHubOwner = v);
            Env("GITSYNC_GITLAB_TOKEN", v => s.Sync.GitLabToken = v);
            Env("GITSYNC_GITLAB_URL", v => s.Sync.GitLabUrl = v);
            Env("GITSYNC_GITLAB_GROUP", v => s.Sync.GitLabGroup = v);
            Env("GITSYNC_GITLAB_NAMESPACE", v => s.Sync.GitLabTargetNamespace = v);
            Env("GITSYNC_AUTO_SYNC_MINUTES", v => { if (int.TryParse(v, out var m)) s.AutoSyncMinutes = m; });
            return s;
        }
    }

    public void Save(ServerSettings s)
    {
        lock (_gate)
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void Env(string name, Action<string> apply)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(v)) apply(v.Trim());
    }
}
