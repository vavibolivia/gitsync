using System.Text;

namespace GitSync.Core;

/// <summary>
/// A local bare repository per pair that holds both sides' refs:
///   refs/remotes/github/heads/*, refs/remotes/github/tags/*
///   refs/remotes/gitlab/heads/*, refs/remotes/gitlab/tags/*
/// Comparison and pushes are done from these refs.
/// </summary>
internal sealed class GitMirror(SyncOptions options, SyncLogger log)
{
    public string AuthHeader(Provider p)
    {
        var token = (p == Provider.GitHub ? options.GitHubToken : options.GitLabToken).Trim();
        var user = p == Provider.GitHub ? "x-access-token" : "oauth2";
        return "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
    }

    public async Task<string> EnsureAsync(string pairId, CancellationToken ct)
    {
        var safe = new StringBuilder();
        foreach (var ch in pairId)
            safe.Append(char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_');
        var dir = Path.Combine(options.CacheRoot, safe + ".git");
        if (!File.Exists(Path.Combine(dir, "HEAD")))
        {
            Directory.CreateDirectory(dir);
            var r = await GitCli.RunAsync(null, ["init", "--bare", "--quiet", dir], null, ct);
            if (!r.Ok) throw new SyncException("로컬 캐시 저장소 생성 실패", r.StdErr);
        }
        return dir;
    }

    public async Task FetchAsync(string dir, RemoteRepo repo, string scope, CancellationToken ct)
    {
        var p = repo.Provider.RemoteName();
        log.Command($"git fetch {repo.CloneUrl}", scope);
        var r = await GitCli.RunAsync(dir,
            ["fetch", "--prune", "--no-tags", "--force", "--quiet", repo.CloneUrl,
             $"+refs/heads/*:refs/remotes/{p}/heads/*",
             $"+refs/tags/*:refs/remotes/{p}/tags/*"],
            AuthHeader(repo.Provider), ct);
        if (!r.Ok)
            throw new SyncException($"{repo.Provider.Label()}에서 가져오기 실패 ({repo.FullName})", r.StdErr);
    }

    public async Task<Dictionary<string, CommitInfo>> ReadHeadsAsync(string dir, Provider p, CancellationToken ct)
    {
        var r = await GitCli.RunAsync(dir,
            ["for-each-ref",
             "--format=%(refname:strip=4)%1f%(objectname)%1f%(authorname)%1f%(committerdate:iso-strict)%1f%(contents:subject)",
             $"refs/remotes/{p.RemoteName()}/heads/"], null, ct);
        if (!r.Ok) throw new SyncException("브랜치 목록 읽기 실패", r.StdErr);

        var map = new Dictionary<string, CommitInfo>(StringComparer.Ordinal);
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\u001f');
            if (f.Length < 2 || f[0] == "HEAD") continue;
            DateTimeOffset? date = f.Length > 3 && DateTimeOffset.TryParse(f[3], out var d) ? d : null;
            map[f[0]] = new CommitInfo(f[1], f.Length > 2 ? f[2] : "", date, f.Length > 4 ? f[4] : "");
        }
        return map;
    }

    public async Task<Dictionary<string, string>> ReadTagsAsync(string dir, Provider p, CancellationToken ct)
    {
        var r = await GitCli.RunAsync(dir,
            ["for-each-ref", "--format=%(refname:strip=4)%1f%(objectname)", $"refs/remotes/{p.RemoteName()}/tags/"], null, ct);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!r.Ok) return map;
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\u001f');
            if (f.Length == 2) map[f[0]] = f[1];
        }
        return map;
    }

    /// <summary>Returns (commits only in GitHub, commits only in GitLab) for a branch.</summary>
    public async Task<(int GitHub, int GitLab)> CountAsync(string dir, string branch, CancellationToken ct)
    {
        var r = await GitCli.RunAsync(dir,
            ["rev-list", "--left-right", "--count",
             $"refs/remotes/github/heads/{branch}...refs/remotes/gitlab/heads/{branch}"], null, ct);
        if (!r.Ok) throw new SyncException($"'{branch}' 비교 실패", r.StdErr);
        var parts = r.StdOut.Trim().Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    public async Task<GitResult> PushAsync(string dir, RemoteRepo target, Provider from,
        IReadOnlyCollection<string> branches, bool force, string scope, CancellationToken ct)
    {
        var src = from.RemoteName();
        var args = new List<string> { "push", "--porcelain" };
        if (force) args.Add("--force");
        args.Add(target.CloneUrl);
        args.AddRange(branches.Select(b => $"refs/remotes/{src}/heads/{b}:refs/heads/{b}"));
        log.Command($"git push {(force ? "--force " : "")}{target.CloneUrl} ({string.Join(", ", branches)})", scope);
        var r = await GitCli.RunAsync(dir, args, AuthHeader(target.Provider), ct);
        if (r.Ok)
        {
            // Mirror the pushed state into the target's tracking refs so a re-compare is accurate without re-fetching.
            foreach (var b in branches)
                await GitCli.RunAsync(dir, ["update-ref", $"refs/remotes/{target.Provider.RemoteName()}/heads/{b}",
                    $"refs/remotes/{src}/heads/{b}"], null, ct);
        }
        return r;
    }

    public async Task<GitResult> PushTagsAsync(string dir, RemoteRepo target, Provider from, IReadOnlyCollection<string> tags,
        string scope, CancellationToken ct)
    {
        var src = from.RemoteName();
        var args = new List<string> { "push", "--porcelain", target.CloneUrl };
        args.AddRange(tags.Select(t => $"refs/remotes/{src}/tags/{t}:refs/tags/{t}"));
        log.Command($"git push {target.CloneUrl} (태그 {tags.Count}개)", scope);
        var r = await GitCli.RunAsync(dir, args, AuthHeader(target.Provider), ct);
        if (r.Ok)
            foreach (var t in tags)
            await GitCli.RunAsync(dir, ["update-ref", $"refs/remotes/{target.Provider.RemoteName()}/tags/{t}",
                $"refs/remotes/{src}/tags/{t}"], null, ct);
        return r;
    }
}
