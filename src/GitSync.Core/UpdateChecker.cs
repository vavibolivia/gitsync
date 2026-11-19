using System.Text.Json;

namespace GitSync.Core;

/// <summary>A release that is newer than the running version.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256);

/// <summary>Looks up the latest GitHub release of GitSync itself (public API, no token is sent).</summary>
public static class UpdateChecker
{
    public const string DefaultApi = "https://api.github.com";
    public const string DefaultRepo = "vavibolivia/gitsync";
    public const string AssetName = "GitSync.exe";

    /// <summary>Parses "v1.2.3", "1.2.3", "1.2" or "1.2.3-beta+sha" into a three-part version.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var s = (text ?? "").Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!s.Contains('.')) s += ".0";
        if (!Version.TryParse(s, out var v)) return false;
        version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        return true;
    }

    /// <summary>
    /// Reads the JSON of <c>GET /repos/{repo}/releases/latest</c>. Returns null unless the release is newer than
    /// <paramref name="current"/>, is not a draft/prerelease and carries the <see cref="AssetName"/> asset.
    /// </summary>
    public static UpdateInfo? ParseLatest(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return null;
        var tag = Str(root, "tag_name");
        if (!TryParseVersion(tag, out var latest) || latest <= new Version(current.Major, current.Minor, Math.Max(0, current.Build)))
            return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = Str(a, "browser_download_url");
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
            var size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
            var digest = Str(a, "digest");
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].Trim() : null;
            return new UpdateInfo(latest, tag!, url, size, sha);
        }
        return null;
    }

    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, Version current, string? apiBase = null, string? repo = null,
        CancellationToken ct = default)
    {
        var url = $"{(string.IsNullOrWhiteSpace(apiBase) ? DefaultApi : apiBase.Trim().TrimEnd('/'))}/repos/" +
                  $"{(string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim())}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("GitSync-Updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release yet
        if (!resp.IsSuccessStatusCode)
            throw new SyncException($"업데이트 확인 실패: HTTP {(int)resp.StatusCode}");
        return ParseLatest(await resp.Content.ReadAsStringAsync(ct), current);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
