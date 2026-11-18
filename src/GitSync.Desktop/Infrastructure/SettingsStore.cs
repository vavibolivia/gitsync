using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitSync.Core;

namespace GitSync.Desktop.Infrastructure;

/// <summary>Persists options to %APPDATA%\GitSync\settings.json. Tokens are encrypted with Windows DPAPI (current user).</summary>
public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitSync");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class Dto
    {
        public SyncOptions Options { get; set; } = new();
        public string? GitHubTokenProtected { get; set; }
        public string? GitLabTokenProtected { get; set; }
    }

    public static SyncOptions Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new SyncOptions();
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath), Json) ?? new Dto();
            var o = dto.Options;
            o.GitHubToken = Unprotect(dto.GitHubTokenProtected);
            o.GitLabToken = Unprotect(dto.GitLabTokenProtected);
            if (string.IsNullOrWhiteSpace(o.CacheRoot)) o.CacheRoot = new SyncOptions().CacheRoot;
            return o;
        }
        catch
        {
            return new SyncOptions();
        }
    }

    public static void Save(SyncOptions options)
    {
        Directory.CreateDirectory(Dir);
        var copy = options.Clone();
        var dto = new Dto
        {
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
