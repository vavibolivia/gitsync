using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitSync.Core;

namespace GitSync.Desktop.Infrastructure;

public sealed class DesktopSettings
{
    /// <summary>"Local" = git runs on this PC. "Server" = drive a GitSync server.</summary>
    public string Mode { get; set; } = "Local";
    public string ServerUrl { get; set; } = "";
    public string ServerKey { get; set; } = "";
    /// <summary>Install newer GitHub releases of GitSync automatically.</summary>
    public bool AutoUpdate { get; set; } = true;
    public SyncOptions Options { get; set; } = new();
    public bool IsServer => Mode == "Server";
}

/// <summary>
/// Persists settings to %APPDATA%\GitSync\settings.json (override the folder with GITSYNC_SETTINGS_DIR).
/// Tokens and the server access key are encrypted with Windows DPAPI (current user only).
/// </summary>
public static class SettingsStore
{
    private static readonly string Dir = Environment.GetEnvironmentVariable("GITSYNC_SETTINGS_DIR") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitSync");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class Dto
    {
        public string Mode { get; set; } = "Local";
        public string ServerUrl { get; set; } = "";
        public string? ServerKeyProtected { get; set; }
        public SyncOptions Options { get; set; } = new();
        public string? GitHubTokenProtected { get; set; }
        public string? GitLabTokenProtected { get; set; }
        public bool AutoUpdate { get; set; } = true;
    }

    public static DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new DesktopSettings();
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath), Json) ?? new Dto();
            var o = dto.Options;
            o.GitHubToken = Unprotect(dto.GitHubTokenProtected);
            o.GitLabToken = Unprotect(dto.GitLabTokenProtected);
            if (string.IsNullOrWhiteSpace(o.CacheRoot)) o.CacheRoot = new SyncOptions().CacheRoot;
            return new DesktopSettings
            {
                Mode = dto.Mode == "Server" ? "Server" : "Local",
                ServerUrl = dto.ServerUrl ?? "",
                ServerKey = Unprotect(dto.ServerKeyProtected),
                AutoUpdate = dto.AutoUpdate,
                Options = o,
            };
        }
        catch
        {
            // Never lose what the user entered: keep the unreadable file aside instead of overwriting it on the next save.
            try { File.Copy(FilePath, FilePath + ".unreadable", overwrite: false); } catch { /* already kept or gone */ }
            return new DesktopSettings();
        }
    }

    public static void Save(DesktopSettings s)
    {
        Directory.CreateDirectory(Dir);
        var copy = s.Options.Clone();
        var dto = new Dto
        {
            Mode = s.Mode,
            ServerUrl = s.ServerUrl,
            ServerKeyProtected = Protect(s.ServerKey),
            AutoUpdate = s.AutoUpdate,
            GitHubTokenProtected = Protect(copy.GitHubToken),
            GitLabTokenProtected = Protect(copy.GitLabToken),
            Options = copy,
        };
        copy.GitHubToken = "";
        copy.GitLabToken = "";
        File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, Json));
    }

    /// <summary>Copies settings.json to settings.json.bak (called before the app replaces itself with a newer version).</summary>
    public static void Backup()
    {
        try { if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true); }
        catch { /* best effort */ }
    }

    private static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }
}
