using GitSync.Core;

namespace GitSync.Server;

public sealed record JobInfo(string Name, bool Running, string Message, double? Progress, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);

/// <summary>
/// Holds the server-side state (settings, repository pairs, logs) and runs one job at a time.
/// </summary>
public sealed class SyncHost : IDisposable
{
    private readonly SettingsFile _file;
    private readonly object _gate = new();
    private SyncService _service;
    private CancellationTokenSource? _jobCts;
    private List<RepoPair> _pairs = new();

    public SyncLogger Log { get; } = new();
    public ServerSettings Settings { get; private set; }
    public JobInfo? Job { get; private set; }
    public string? GitHubUser { get; private set; }
    public string? GitLabUser { get; private set; }
    public DateTimeOffset? LastScan { get; private set; }
    public DateTimeOffset? NextAutoSync { get; set; }

    public SyncHost(SettingsFile file)
    {
        _file = file;
        Settings = file.Load();
        _service = new SyncService(Settings.Sync, Log);
        Log.Info("GitSync 서버 시작", "server");
    }

    public IReadOnlyList<RepoPair> Pairs => _pairs;
    public bool IsBusy => Job?.Running == true;
    public bool IsConfigured => Settings.Sync.IsGitHubConfigured && Settings.Sync.IsGitLabConfigured;
    public RepoPair? Find(string id) => _pairs.FirstOrDefault(p => p.Id == id);

    public void UpdateSettings(ServerSettings next)
    {
        lock (_gate)
        {
            if (IsBusy) throw new InvalidOperationException("작업이 실행 중일 때는 설정을 바꿀 수 없습니다.");
            _file.Save(next);
            Settings = _file.Load();
            _service.Dispose();
            _service = new SyncService(Settings.Sync, Log);
            GitHubUser = GitLabUser = null;
            NextAutoSync = null;
        }
        Log.Info("설정 저장", "settings");
    }

    public SyncService CreateService(SyncOptions options) => new(options, Log);

    /// <summary>Starts a background job. Returns false if another job is already running.</summary>
    public bool TryStart(string name, Func<SyncService, Action<string, double?>, CancellationToken, Task> work)
    {
        lock (_gate)
        {
            if (IsBusy) return false;
            _jobCts = new CancellationTokenSource();
            Job = new JobInfo(name, true, "시작 중…", null, DateTimeOffset.Now, null, null);
        }
        var svc = _service;
        var ct = _jobCts.Token;
        void Report(string msg, double? pct) => Job = Job! with { Message = msg, Progress = pct };

        _ = Task.Run(async () =>
        {
            string? error = null;
            try { await work(svc, Report, ct); }
            catch (OperationCanceledException) { error = "취소됨"; Log.Warn($"{name} 취소됨", "job"); }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error(ex.Message, "job", (ex as SyncException)?.Detail);
            }
            finally
            {
                Job = Job! with { Running = false, FinishedAt = DateTimeOffset.Now, Error = error, Message = error ?? "완료" };
            }
        });
        return true;
    }

    public void Cancel() => _jobCts?.Cancel();

    // ───────────────────────── job bodies ─────────────────────────

    public async Task ScanAsync(SyncService svc, Action<string, double?> report, CancellationToken ct)
    {
        report("계정 확인 중…", null);
        GitHubUser = await svc.TestGitHubAsync(ct);
        GitLabUser = await svc.TestGitLabAsync(ct);
        report("모든 저장소 검색 중…", null);
        var pairs = await svc.ScanAsync(ct);
        _pairs = pairs;
        await ForEachAsync(pairs.Where(p => p.HasBoth).ToList(), 6, "브랜치 비교", p => svc.QuickCompareAsync(p, ct), report, ct);
        LastScan = DateTimeOffset.Now;
    }

    public Task AnalyzeAsync(SyncService svc, IReadOnlyList<RepoPair> targets, Action<string, double?> report, CancellationToken ct) =>
        ForEachAsync(targets.Where(p => p.HasBoth).ToList(), svc.Options.MaxParallel, "정밀 분석",
            async p => { try { await svc.AnalyzeAsync(p, null, ct); } catch (SyncException) { /* logged */ } }, report, ct);

    public async Task SyncAsync(SyncService svc, IReadOnlyList<RepoPair> targets, Provider from, Action<string, double?> report, CancellationToken ct)
    {
        var results = new List<SyncResult>();
        await ForEachAsync(targets.Where(p => p.Get(from) is not null).ToList(), svc.Options.MaxParallel,
            $"{from.Other().Label()}로 동기화",
            async p =>
            {
                var r = await svc.SyncAsync(p, from, null, ct);
                lock (results) results.Add(r);
            }, report, ct);
        Log.Write(results.All(r => r.Ok) ? SyncLogLevel.Success : SyncLogLevel.Warning,
            $"일괄 동기화 완료 · 생성 {results.Count(r => r.CreatedRepo)} · 브랜치 푸시 {results.Sum(r => r.Pushed)} · 실패 {results.Count(r => !r.Ok)}",
            "bulk");
    }

    private static async Task ForEachAsync(IReadOnlyList<RepoPair> items, int parallel, string label,
        Func<RepoPair, Task> work, Action<string, double?> report, CancellationToken ct)
    {
        if (items.Count == 0) return;
        int done = 0;
        report($"{label} 0/{items.Count}", 0);
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallel, 1, 8), CancellationToken = ct },
            async (item, _) =>
            {
                await work(item);
                var n = Interlocked.Increment(ref done);
                report($"{label} {n}/{items.Count}", n * 100.0 / items.Count);
            });
    }

    public void Dispose() => _service.Dispose();
}

/// <summary>Optional periodic scan + GitHub → GitLab sync.</summary>
public sealed class AutoSyncWorker(SyncHost host) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var minutes = host.Settings.AutoSyncMinutes;
            if (minutes <= 0 || !host.IsConfigured) { host.NextAutoSync = null; continue; }
            host.NextAutoSync ??= DateTimeOffset.Now.AddMinutes(minutes);
            if (DateTimeOffset.Now < host.NextAutoSync || host.IsBusy) continue;

            host.NextAutoSync = DateTimeOffset.Now.AddMinutes(minutes);
            host.Log.Info($"자동 동기화 시작 (매 {minutes}분)", "auto");
            host.TryStart("자동 동기화", async (svc, report, ct) =>
            {
                await host.ScanAsync(svc, report, ct);
                await host.SyncAsync(svc, host.Pairs, Provider.GitHub, report, ct);
            });
        }
    }
}
