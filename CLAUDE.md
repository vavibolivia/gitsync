# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

GitSync compares and synchronizes every repository between GitHub and GitLab (both directions), branch by branch. Requirements: .NET 10 SDK, git 2.31+ on PATH. User-facing strings, logs, docs and error messages are in Korean — keep them that way.

## Commands

```bash
dotnet build GitSync.sln
dotnet test tests/GitSync.Tests                                   # all integration tests (~1 min, needs git on PATH)
dotnet test tests/GitSync.Tests --filter "FullyQualifiedName~Scan_finds_all"   # single test
dotnet test tests/GitSync.Tests --filter "FullyQualifiedName~ServerTests"     # one class

dotnet run --project src/GitSync.Desktop                          # WPF app (Windows only)
GITSYNC_WEB_ROOT="$PWD/web" dotnet run --project src/GitSync.Server   # API + web UI without nginx (must be an absolute path: dotnet run uses the project dir as cwd)

# single-file exe (as CI does)
dotnet publish src/GitSync.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish

bash -n deploy/deploy.sh                                          # syntax check the deploy script
```

There is no separate linter; the build is the check. `publish/` is gitignored.

## Architecture

- **`src/GitSync.Core`** — the engine, shared by every front end.
  - `ISyncBackend` is what the UIs consume. Two implementations: `SyncService` (git runs on this machine) and `RemoteSyncClient` (forwards to a GitSync server over its HTTP API; bulk analyze/sync become a single server job). The WPF app picks one based on Settings → 실행 위치.
  - `ApiClients.cs` — GitHub REST and GitLab REST v4 clients (listing with pagination, branch head SHAs, repo/project creation, default branch).
  - `GitMirror` — one local **bare mirror per repo pair** in the cache dir, holding both sides as `refs/remotes/github/{heads,tags}/*` and `refs/remotes/gitlab/{heads,tags}/*`. Exact ahead/behind (`git rev-list --left-right --count`) and all pushes run from these refs.
  - `GitCli` — runs git. Tokens are passed **only** via `GIT_CONFIG_KEY_n/VALUE_n` env vars (`http.extraHeader`, empty `credential.helper`), never on the command line or in git config; prompts are disabled. Tests assert tokens never leak into the cache, logs or API responses — preserve this.
  - Flow: `ScanAsync` (list + pair by name, name mappings, namespace preference) → `QuickCompareAsync` (API head SHAs only) → `AnalyzeAsync` (fetch into mirror, exact counts) → `SyncAsync` using `BuildPlan`.
  - **Sync safety rules** (`SyncService.BuildPlan`): missing branch → create; target behind → fast-forward; target ahead or diverged → skip (only an explicit per-branch `PushBranchAsync(force: true)` overwrites); branches only on the target are never touched. Missing target repos are created when `CreateMissingRepos` is on. Default branch is pushed first. Re-running must be a no-op.
- **`src/GitSync.Server`** — ASP.NET Core minimal API (`Program.cs`). `SyncHost` holds state and allows **one job at a time** (concurrent requests → 409); `AutoSyncWorker` does periodic sync. Settings live in `$GITSYNC_DATA/settings.json`; `GITSYNC_*` env vars override (see `ServerSettings.cs`). Tokens are write-only through `/api/settings` (an empty value keeps the existing token). If `GITSYNC_ACCESS_KEY` is set, every endpoint except `/api/health` and static files requires an `X-Access-Key` header.
- **`src/GitSync.Desktop`** — WPF MVVM (hand-rolled `Infrastructure/Mvvm.cs`, no framework). Settings, tokens and the server access key are stored with DPAPI encryption in `SettingsStore`.
- **`web/`** — plain static HTML/CSS/JS (no build step) served by nginx in production, talking to `/api`.
- **`deploy/`** — `deploy.sh` / `deploy.ps1` build `NAME-api` and `NAME-web` images and run them in `NAME-pod` with podman. Before building it deletes any existing image of the same name (all tags) and the containers/pod that use it; the data dir is kept. `-H user@host` copies the source to a remote host and builds there.

## Tests

`tests/GitSync.Tests` are integration tests, not mocks:
- `FakeForge` is an in-process fake of the GitHub REST API, the GitLab REST API and git smart-HTTP (backed by real bare repos through `git http-backend`), so auth headers, fetch/push, pagination and repo creation are all exercised for real.
- `ServerTests` start the **real `GitSync.Server` process** (the test project builds it but does not reference its assembly) and drive the HTTP API and `RemoteSyncClient` against it.

CI (`.github/workflows/build.yml`): a Windows job builds, tests and publishes `GitSync.exe` (a release is made on `v*` tags); a `deploy-e2e` Ubuntu job runs `deploy.sh` against real podman + nginx and checks redeploy behavior.

`docs/QA-PLAN.md` holds the QA case list and results (Korean). Update it when you add or change test cases.
