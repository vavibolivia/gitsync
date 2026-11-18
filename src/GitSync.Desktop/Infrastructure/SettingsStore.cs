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
                Options = o,
            };
        }
        catch
        {
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
            GitHubTokenProtected = Protect(copy.GitHubToken),
            GitLabTokenProtected = Protect(copy.GitLabToken),
            Options = copy,
        };
        copy.GitHubToken = "";
        copy.GitLabToken = "";
        File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, Json));
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
