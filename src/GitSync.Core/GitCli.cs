using System.Diagnostics;
using System.Text;

namespace GitSync.Core;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs the git CLI. Credentials are injected through GIT_CONFIG_* environment variables,
/// so tokens never appear on the command line or in stored git config.</summary>
public static class GitCli
{
    public static async Task<GitResult> RunAsync(string? workDir, IEnumerable<string> args,
        string? authHeader = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        var cfg = new List<(string Key, string Value)>
        {
            ("core.quotepath", "false"),
            ("i18n.logOutputEncoding", "UTF-8"),
        };
        if (authHeader is not null)
        {
            cfg.Add(("credential.helper", ""));
            cfg.Add(("http.extraHeader", authHeader));
        }
        for (int i = 0; i < cfg.Count; i++)
        {
            psi.Environment[$"GIT_CONFIG_KEY_{i}"] = cfg[i].Key;
            psi.Environment[$"GIT_CONFIG_VALUE_{i}"] = cfg[i].Value;
        }
        psi.Environment["GIT_CONFIG_COUNT"] = cfg.Count.ToString();

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new SyncException("git 프로세스를 시작할 수 없습니다.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SyncException("git 실행 파일을 찾을 수 없습니다. Git이 설치되어 PATH에 있는지 확인하세요.");
        }

        using (p)
        {
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }
            return new GitResult(p.ExitCode, await outTask, await errTask);
        }
    }
}
