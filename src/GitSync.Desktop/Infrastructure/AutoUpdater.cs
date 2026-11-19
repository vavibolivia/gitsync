using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using GitSync.Core;

namespace GitSync.Desktop.Infrastructure;

/// <summary>
/// Self-update for the single-file GitSync.exe: when a newer GitHub release exists, download its GitSync.exe next to
/// the running one, verify it (size, SHA-256 from the release, PE header), swap the files and restart.
/// A running exe cannot be overwritten on Windows but it can be renamed, so the old file becomes "GitSync.exe.old"
/// and is deleted on the next start.
/// </summary>
public static class AutoUpdater
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>Only the published single-file exe replaces itself (not a build run from the IDE or "dotnet run").</summary>
    public static bool CanSelfUpdate =>
        string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location) &&
        ExePath is { } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        Environment.GetEnvironmentVariable("GITSYNC_NO_UPDATE") is not "1";

    /// <summary>Removes the previous version left behind by the last update.</summary>
    public static void CleanupOld()
    {
        if (ExePath is not { } exe) return;
        _ = Task.Run(async () =>
        {
            foreach (var leftover in new[] { exe + ".old", exe + ".new" })
                for (int i = 0; i < 10 && File.Exists(leftover); i++)
                {
                    try { File.Delete(leftover); }
                    catch { await Task.Delay(1000); }   // the old process may still be exiting
                }
        });
    }

    public static Task<UpdateInfo?> CheckAsync(CancellationToken ct = default) =>
        UpdateChecker.CheckAsync(Http, CurrentVersion,
            Environment.GetEnvironmentVariable("GITSYNC_UPDATE_API"),
            Environment.GetEnvironmentVariable("GITSYNC_UPDATE_REPO"), ct);

    /// <summary>Downloads and verifies the new exe. Returns its path (next to the running exe).</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        var exe = ExePath ?? throw new SyncException("실행 파일 경로를 알 수 없습니다.");
        var target = exe + ".new";
        try
        {
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new SyncException($"업데이트 파일을 받을 수 없습니다: HTTP {(int)resp.StatusCode}");
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(target);
                await src.CopyToAsync(dst, ct);
            }

            var length = new FileInfo(target).Length;
            if (info.Size > 0 && length != info.Size)
                throw new SyncException($"업데이트 파일 크기가 다릅니다 ({length} / {info.Size}).");

            await using (var fs = File.OpenRead(target))
            {
                var header = new byte[2];
                if (await fs.ReadAsync(header, ct) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
                    throw new SyncException("업데이트 파일이 실행 파일이 아닙니다.");
                if (info.Sha256 is { Length: > 0 } expected)
                {
                    fs.Position = 0;
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new SyncException("업데이트 파일의 SHA-256 해시가 릴리스 정보와 다릅니다.");
                }
            }
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>Swaps the exe files and starts the new version. The caller shuts the application down afterwards.</summary>
    public static void ApplyAndRestart(string newExe, UpdateInfo info)
    {
        var exe = ExePath ?? throw new SyncException("실행 파일 경로를 알 수 없습니다.");
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newExe, exe);
        }
        catch
        {
            File.Move(old, exe);   // roll back so the app still starts next time
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Arguments = $"--updated-to {info.Tag}" });
    }
}
