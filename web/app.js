// GitSync web UI — talks to the GitSync server through /api (proxied by nginx).
(() => {
  "use strict";

  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const esc = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  const icon = (id, cls = "") => `<svg class="${cls}"><use href="#i-${id}"/></svg>`;

  const STATUS_TEXT = {
    Unknown: "확인 전", Synced: "동기화됨", OutOfSync: "차이 있음", GitHubOnly: "GitHub에만", GitLabOnly: "GitLab에만",
    Empty: "빈 저장소", Error: "오류", Differs: "다름 · 분석 필요", GitHubAhead: "GitHub 앞섬", GitLabAhead: "GitLab 앞섬", Diverged: "분기됨",
  };
  const LOG_ICON = { Success: "check", Warning: "warn", Error: "error", Command: "term", Info: "info" };

  const state = {
    key: safeGet("gitsync.key") || "",
    page: "repos",
    status: null,
    repos: [],
    selectedId: safeGet("gitsync.selected") || null,
    detail: null,
    filter: "All",
    search: "",
    logs: [],
    lastLogId: 0,
    wasRunning: false,
    settings: null,
    dirty: false,
  };

  function safeGet(k) { try { return localStorage.getItem(k); } catch { return null; } }
  function safeSet(k, v) { try { v == null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch { /* ignore */ } }

  // ───────────────────────── API ─────────────────────────

  async function api(path, opts = {}) {
    const headers = { "Content-Type": "application/json" };
    if (state.key) headers["X-Access-Key"] = state.key;
    const res = await fetch("/api" + path, { ...opts, headers: { ...headers, ...(opts.headers || {}) } });
    if (res.status === 401) {
      const key = await prompt("접근 키 필요", "서버에 설정된 GITSYNC_ACCESS_KEY 값을 입력하세요.");
      if (key) { state.key = key; safeSet("gitsync.key", key); return api(path, opts); }
      throw new Error("접근 키가 필요합니다.");
    }
    const text = await res.text();
    const data = text ? JSON.parse(text) : null;
    if (!res.ok) throw new Error(data?.error || `요청 실패 (${res.status})`);
    return data;
  }
  const post = (path, body) => api(path, { method: "POST", body: JSON.stringify(body ?? {}) });

  async function startJob(path, body, label) {
    try {
      await post(path, body);
      toast(`${label} 시작`, "Info");
      await refreshStatus();
    } catch (e) { toast(e.message, "Error"); }
  }

  // ───────────────────────── status / polling ─────────────────────────

  async function refreshStatus() {
    let s;
    try { s = await api("/status"); } catch (e) { setOffline(e.message); return; }
    state.status = s;
    renderStatus();
    const running = !!s.job?.running;
    if (state.wasRunning && !running) {
      await loadRepos();
      if (s.job?.error) toast(`${s.job.name}: ${s.job.error}`, "Error");
      else toast(`${s.job.name} 완료`, "Success");
    }
    state.wasRunning = running;
    if (s.lastLogId > state.lastLogId) await loadLogs();
  }

  function setOffline(msg) {
    $("#jobText").textContent = "서버에 연결할 수 없습니다 — " + msg;
    $("#jobIdle").className = "dot";
  }

  async function poll() {
    await refreshStatus();
    if (state.status?.job?.running && state.page === "repos") await loadRepos(true);
    setTimeout(poll, state.status?.job?.running ? 1500 : 6000);
  }

  async function loadRepos(quiet) {
    try {
      state.repos = await api("/repos");
      renderList();
      if (state.selectedId && state.repos.some((r) => r.id === state.selectedId)) await loadDetail(state.selectedId, quiet);
      else { state.selectedId = state.repos[0]?.id ?? null; if (state.selectedId) await loadDetail(state.selectedId); else renderDetail(); }
    } catch (e) { if (!quiet) toast(e.message, "Error"); }
  }

  async function loadDetail(id, quiet) {
    try {
      state.detail = await api("/repo?id=" + encodeURIComponent(id));
    } catch (e) { state.detail = null; if (!quiet) toast(e.message, "Error"); }
    renderDetail();
  }

  async function loadLogs() {
    try {
      const items = await api("/logs?after=" + state.lastLogId);
      if (!items.length) return;
      state.logs.push(...items);
      if (state.logs.length > 2000) state.logs.splice(0, state.logs.length - 2000);
      state.lastLogId = items[items.length - 1].id;
      renderLogs();
    } catch { /* ignore */ }
  }

  // ───────────────────────── rendering ─────────────────────────

  function category(r) {
    if (r.status === "Synced" || r.status === "Empty") return "Synced";
    if (r.status === "GitHubOnly" || r.status === "GitLabOnly") return r.status;
    return "OutOfSync";
  }

  function visibleRepos() {
    const q = state.search.trim().toLowerCase();
    return state.repos.filter((r) =>
      (state.filter === "All" || category(r) === state.filter) &&
      (!q || r.name.toLowerCase().includes(q) || r.gitHub?.fullName?.toLowerCase().includes(q) || r.gitLab?.fullName?.toLowerCase().includes(q)));
  }

  function renderStatus() {
    const s = state.status;
    if (!s) return;
    $("#ghHost").textContent = s.githubHost === "github.com" ? "GitHub" : s.githubHost;
    $("#glHost").textContent = s.gitlabHost;
    $("#ghUser").textContent = s.githubUser || "연결 안 됨";
    $("#glUser").textContent = s.gitlabUser || "연결 안 됨";
    $("#ghDot").className = "dot" + (s.githubUser ? " on" : "");
    $("#glDot").className = "dot" + (s.gitlabUser ? " on" : "");

    for (const [k, v] of Object.entries(s.counts)) { const el = $(`[data-count="${k}"]`); if (el) el.textContent = v; }
    const nd = $("#navDiff"); nd.hidden = !s.counts.outOfSync; nd.textContent = s.counts.outOfSync;

    const auto = $("#autoInfo");
    auto.hidden = !s.autoSyncMinutes;
    if (s.autoSyncMinutes) auto.querySelector("span").textContent =
      `자동 동기화 매 ${s.autoSyncMinutes}분` + (s.nextAutoSync ? ` · 다음 ${fmtTime(s.nextAutoSync)}` : "");

    const job = s.job;
    const running = !!job?.running;
    $("#jobSpin").hidden = !running;
    $("#jobIdle").hidden = running;
    $("#jobIdle").className = "dot ok";
    $("#jobText").textContent = running ? `${job.name} · ${job.message}` : (job?.error ? `${job.name}: ${job.error}` : "준비됨");
    $("#jobProg").hidden = !(running && job.progress != null);
    $("#jobProg i").style.width = (job?.progress ?? 0) + "%";
    $("#cancelJob").hidden = !running;
    $$("[data-action]").forEach((b) => (b.disabled = running));
    $$(".detail [data-act]").forEach((b) => (b.disabled = running || b.dataset.disabled === "1"));
    $("#metaText").textContent = (s.lastScan ? `마지막 스캔 ${fmtTime(s.lastScan)}` : "아직 스캔하지 않음") + `  ·  ${s.counts.total}개 저장소`;

    if (!state.detail) renderDetail();   // empty state depends on `configured`
  }

  function renderList() {
    const list = $("#repoList");
    const items = visibleRepos();
    if (!state.repos.length) {
      list.innerHTML = `<div class="empty">${icon("repos")}<h3>저장소가 없습니다</h3><div>설정에서 토큰을 입력한 뒤 '전체 검색'을 누르세요.</div></div>`;
      return;
    }
    if (!items.length) { list.innerHTML = `<div class="empty"><div>조건에 맞는 저장소가 없습니다.</div></div>`; return; }
    list.innerHTML = items.map((r) => `
      <div class="repo ${r.id === state.selectedId ? "selected" : ""}" data-id="${esc(r.id)}">
        <div class="repo-main">
          <div class="repo-name">${esc(r.name)}</div>
          <div class="repo-sub">${icon("github", r.gitHub ? "" : "off")}${icon("gitlab", r.gitLab ? "gl" : "off")}<span>${esc(subtitle(r))}</span></div>
        </div>
        <span class="pill s-${r.status}">${STATUS_TEXT[r.status] ?? r.status}</span>
      </div>`).join("");
  }

  function subtitle(r) {
    switch (r.status) {
      case "GitHubOnly": return "GitLab에 없음 · 생성 가능";
      case "GitLabOnly": return "GitHub에 없음";
      case "Error": return "오류 발생";
      case "Unknown": return "비교 대기 중";
      case "Empty": return "브랜치 없음";
      default: return r.diffCount ? `브랜치 ${r.branchCount}개 · ${r.diffCount}개 다름` : `브랜치 ${r.branchCount}개 · 모두 동일`;
    }
  }

  function renderDetail() {
    const el = $("#detail");
    const d = state.detail;
    if (!d) {
      const configured = state.status?.configured;
      el.innerHTML = `<div class="empty"><div class="empty-icon">${icon("sync")}</div>
        <h3>${configured ? "저장소를 선택하세요" : "먼저 설정을 완료하세요"}</h3>
        <div>${configured ? "왼쪽 목록에서 저장소를 선택하면 브랜치별 비교 결과가 표시됩니다." : "설정에서 GitHub · GitLab 토큰을 입력하고 저장하세요."}</div>
        ${configured ? "" : `<button class="btn primary" style="margin-top:14px" onclick="document.querySelector('[data-page=settings]').click()">${icon("gear")}설정으로 이동</button>`}</div>`;
      return;
    }
    const running = !!state.status?.job?.running;
    const both = d.gitHub && d.gitLab;
    const side = (r, kind, missingText) => r
      ? `<a class="side" href="${esc(r.webUrl)}" target="_blank" rel="noopener">${icon(kind, "logo " + (kind === "github" ? "gh" : "gl"))}<div><small>${kind === "github" ? "GitHub" : "GitLab"}</small><span>${esc(r.fullName)}</span></div>${icon("ext", "ext")}</a>`
      : `<div class="side missing">${icon(kind, "logo " + (kind === "github" ? "gh" : "gl"))}<div><small>${kind === "github" ? "GitHub" : "GitLab"}</small><span>${missingText}</span></div></div>`;
    const t = d.tags;
    const tagText = t ? (t.total === 0 ? "태그 없음" : `태그 ${t.total}개 · 동일 ${t.synced}` + (t.gitHubOnly ? ` · GitHub만 ${t.gitHubOnly}` : "") + (t.gitLabOnly ? ` · GitLab만 ${t.gitLabOnly}` : "") + (t.different ? ` · 다름 ${t.different}` : ""))
      : (d.isAnalyzed ? "" : "태그: 정밀 분석 후 표시");
    const checked = d.lastChecked ? `${d.isAnalyzed ? "정밀 분석" : "API 비교"} · ${rel(d.lastChecked)}` : "아직 비교하지 않음";
    const repo = d.gitHub ?? d.gitLab;
    const btn = (act, cls, content, enabled, title) =>
      `<button class="btn ${cls}" data-act="${act}" data-disabled="${enabled ? 0 : 1}" ${!enabled || running ? "disabled" : ""} title="${esc(title)}">${content}</button>`;

    el.innerHTML = `
      <div class="hero">
        <div class="hero-top">
          <h2>${esc(d.name)}</h2>
          <span class="pill s-${d.status}">${STATUS_TEXT[d.status] ?? d.status}</span>
          <span class="pill outline">${repo?.isPrivate ? "Private" : "Public"}</span>
        </div>
        ${repo?.description ? `<div class="desc">${esc(repo.description)}</div>` : ""}
        <div class="sides">${side(d.gitHub, "github", "없음")}${icon("swap")}${side(d.gitLab, "gitlab", "없음 — 생성 가능")}</div>
      </div>
      <div class="toolbar">
        <div class="meta">${esc(checked)}${tagText ? `<br>${esc(tagText)}` : ""}</div>
        <div class="actions">
          ${btn("analyze", "", icon("pulse") + "정밀 분석", both, "양쪽을 가져와 커밋 단위로 정밀 비교")}
          ${btn("syncGitHub", "", icon("github") + "GitHub으로", !!d.gitLab, "GitLab → GitHub 방향으로 푸시")}
          ${btn("syncGitLab", "gitlab", icon("gitlab") + (d.gitLab ? "GitLab으로 동기화" : "GitLab에 생성 + 푸시"), !!d.gitHub, "없으면 저장소/브랜치를 생성하고 업데이트")}
        </div>
      </div>
      ${d.error ? `<div class="banner">${icon("error")}<div>${esc(d.error)}</div></div>` : ""}
      <div class="table">
        ${both ? `
        <div class="thead row-grid"><span>브랜치</span><span>${icon("github")}GITHUB</span><span style="justify-content:center">상태</span><span>${icon("gitlab")}GITLAB</span><span style="justify-content:flex-end">작업</span></div>
        <div class="tbody">${(d.branches || []).map(branchRow).join("") || `<div class="empty"><div>브랜치가 없습니다.</div></div>`}</div>`
        : `<div class="empty">${icon("info")}<h3>${d.gitHub ? "GitLab" : "GitHub"}에 이 저장소가 없습니다</h3><div>위의 동기화 버튼을 누르면 대상에 저장소를 만들고 모든 브랜치와 태그를 복사합니다.</div></div>`}
      </div>`;
  }

  function branchRow(b) {
    const running = !!state.status?.job?.running;
    const short = (s) => (s ? s.slice(0, 7) : "—");
    const meta = (c) => (c ? `<small>${esc(c.author)} · ${rel(c.date)}</small>` : "");
    const ahead = {
      GitHubAhead: `+${b.gitHubAhead} 커밋`, GitLabAhead: `+${b.gitLabAhead} 커밋`, Diverged: `GH +${b.gitHubAhead} · GL +${b.gitLabAhead}`,
      GitHubOnly: "GitLab에 생성 필요", GitLabOnly: "GitHub에 없음", Differs: "SHA 다름",
    }[b.status] || "";
    const canGl = b.gitHubSha && b.status !== "Synced";
    const canGh = b.gitLabSha && b.status !== "Synced";
    const forceGl = b.gitLabSha && b.gitLabAhead > 0;
    const forceGh = b.gitHubSha && b.gitHubAhead > 0;
    return `
      <div class="brow row-grid">
        <div class="bname">${b.isDefault ? icon("star") : ""}<span title="${esc(b.name)}">${esc(b.name)}</span></div>
        <div class="sha" title="${esc(b.gitHubCommit?.subject)}">${short(b.gitHubSha)}${meta(b.gitHubCommit)}</div>
        <div class="bstatus"><span class="pill s-${b.status}">${STATUS_TEXT[b.status] ?? b.status}</span>${ahead ? `<small>${ahead}</small>` : ""}</div>
        <div class="sha" title="${esc(b.gitLabCommit?.subject)}">${short(b.gitLabSha)}${meta(b.gitLabCommit)}</div>
        <div class="bactions">
          <button class="btn small" data-act="push" data-from="gitlab" data-branch="${esc(b.name)}" data-force="${forceGh ? 1 : 0}" data-disabled="${canGh ? 0 : 1}" ${!canGh || running ? "disabled" : ""}
            title="${forceGh ? "GitLab → GitHub 강제 푸시" : b.gitHubSha ? "GitLab → GitHub 푸시" : "GitHub에 브랜치 생성"}">${icon("left")}</button>
          <button class="btn small to-gl" data-act="push" data-from="github" data-branch="${esc(b.name)}" data-force="${forceGl ? 1 : 0}" data-disabled="${canGl ? 0 : 1}" ${!canGl || running ? "disabled" : ""}
            title="${forceGl ? "GitHub → GitLab 강제 푸시" : b.gitLabSha ? "GitHub → GitLab 푸시" : "GitLab에 브랜치 생성"}">${icon("right")}</button>
        </div>
      </div>`;
  }

  function renderLogs() {
    const list = $("#logList");
    const errors = state.logs.filter((l) => l.level === "Error").length;
    const ne = $("#navErr"); ne.hidden = !errors; ne.textContent = errors;
    if (!state.logs.length) { list.innerHTML = `<div class="empty">${icon("log")}<div>기록이 없습니다</div></div>`; return; }
    list.innerHTML = state.logs.slice().reverse().map((l) => `
      <div class="log ${l.level}">
        <time>${fmtTime(l.time)}</time>
        ${icon(LOG_ICON[l.level] || "info", "lv-" + l.level)}
        <div class="scope" title="${esc(l.scope)}">${esc(l.scope)}</div>
        <div><div class="msg">${esc(l.message)}</div>${l.detail ? `<pre>${esc(l.detail)}</pre>` : ""}</div>
      </div>`).join("");
  }

  // ───────────────────────── settings ─────────────────────────

  async function loadSettings() {
    try {
      const s = await api("/settings");
      state.settings = s;
      const f = $("#settingsForm");
      for (const el of f.elements) {
        if (!el.name) continue;
        if (el.type === "checkbox") el.checked = !!s[el.name];
        else if (el.type === "password") { el.value = ""; el.placeholder = (el.name === "gitHubToken" ? s.hasGitHubToken : s.hasGitLabToken) ? "●●●●●●●● 저장됨 — 변경할 때만 입력" : "토큰 입력"; }
        else el.value = s[el.name] ?? "";
      }
      setDirty(false);
    } catch (e) { toast(e.message, "Error"); }
  }

  function readSettings() {
    const out = {};
    for (const el of $("#settingsForm").elements) {
      if (!el.name) continue;
      if (el.type === "checkbox") out[el.name] = el.checked;
      else if (el.type === "number") out[el.name] = el.value === "" ? null : Number(el.value);
      else if (el.type === "password") { if (el.value.trim()) out[el.name] = el.value.trim(); }
      else out[el.name] = el.value;
    }
    return out;
  }

  function setDirty(v) { state.dirty = v; $("#dirtyPill").hidden = !v; }

  async function saveSettings() {
    try {
      await api("/settings", { method: "PUT", body: JSON.stringify(readSettings()) });
      setDirty(false);
      toast("설정을 저장했습니다.", "Success");
      await loadSettings();
      await refreshStatus();
      if (state.status?.configured) { go("repos"); await startJob("/scan", null, "전체 검색"); }
    } catch (e) { toast(e.message, "Error"); }
  }

  async function testConnection(provider) {
    const out = $(`[data-result="${provider}"]`);
    out.className = "test"; out.textContent = "확인 중…";
    try {
      const r = await post("/test/" + provider, readSettings());
      out.className = "test " + (r.ok ? "ok" : "err");
      out.textContent = r.ok ? `✓ 연결됨 · ${r.user}` : `✕ ${r.error}`;
    } catch (e) { out.className = "test err"; out.textContent = "✕ " + e.message; }
  }

  // ───────────────────────── actions ─────────────────────────

  async function onAction(action) {
    const ids = visibleRepos().map((r) => r.id);
    switch (action) {
      case "scan": return startJob("/scan", null, "전체 검색");
      case "analyzeAll": {
        const n = visibleRepos().filter((r) => r.gitHub && r.gitLab).length;
        if (!n) return toast("분석할 저장소가 없습니다 (양쪽에 모두 있는 저장소만).", "Warning");
        return startJob("/analyze", { ids }, "정밀 분석");
      }
      case "syncAllGitLab": return bulkSync("github", ids);
      case "syncAllGitHub": return bulkSync("gitlab", ids);
    }
  }

  async function bulkSync(from, ids) {
    const to = from === "github" ? "GitLab" : "GitHub";
    const fromLabel = from === "github" ? "GitHub" : "GitLab";
    const targets = visibleRepos().filter((r) => (from === "github" ? r.gitHub : r.gitLab));
    if (!targets.length) return toast(`${fromLabel} 쪽 저장소가 없습니다.`, "Warning");
    const create = targets.filter((r) => !(from === "github" ? r.gitLab : r.gitHub)).length;
    const s = state.settings || {};
    const ok = await confirm(`${fromLabel} → ${to} 일괄 동기화`, `현재 목록의 저장소를 ${to}로 동기화합니다.`, "동기화 시작", false, [
      `대상 저장소: ${targets.length}개 (현재 목록 기준)`,
      create ? (s.createMissingRepos !== false ? `${to}에 새로 생성: ${create}개` : `${to}에 없는 ${create}개는 자동 생성이 꺼져 있어 건너뜀`) : `${to}에 없는 저장소 없음`,
      "새 브랜치 생성 + fast-forward 가능한 브랜치 업데이트",
      s.syncTags !== false ? "누락된 태그 푸시" : "태그는 동기화하지 않음",
      "분기된 브랜치는 덮어쓰지 않고 건너뜀",
    ]);
    if (ok) await startJob("/sync", { ids: targets.map((r) => r.id), from }, `${to}로 동기화`);
  }

  async function onDetailAction(btn) {
    const d = state.detail;
    if (!d) return;
    const act = btn.dataset.act;
    if (act === "analyze") return startJob("/analyze", { ids: [d.id] }, "정밀 분석");
    if (act === "syncGitLab" || act === "syncGitHub") {
      const from = act === "syncGitLab" ? "github" : "gitlab";
      const to = from === "github" ? "GitLab" : "GitHub";
      const missing = from === "github" ? !d.gitLab : !d.gitHub;
      const details = missing
        ? [`새 저장소: ${d.name}`, "모든 브랜치와 태그 복사"]
        : (d.branches || []).filter((b) => (from === "github" ? b.gitHubSha : b.gitLabSha) && b.status !== "Synced").map((b) => {
            const tgt = from === "github" ? b.gitLabSha : b.gitHubSha;
            const tgtAhead = from === "github" ? b.gitLabAhead : b.gitHubAhead;
            if (!tgt) return `＋ ${b.name}  (새 브랜치)`;
            if (b.status === "Differs") return `? ${b.name}  (분석 후 결정)`;
            return tgtAhead > 0 ? `⨯ ${b.name}  건너뜀 — ${b.status === "Diverged" ? "분기됨" : to + " 쪽이 앞섬"}` : `↑ ${b.name}`;
          });
      const ok = await confirm(missing ? `${to}에 저장소 생성` : `${to}로 동기화`,
        missing ? `'${d.name}' 저장소를 ${to}에 만들고 내용을 복사합니다.` : `'${d.name}'의 새 브랜치와 fast-forward 가능한 브랜치를 ${to}로 푸시합니다.`,
        missing ? "생성 후 동기화" : "푸시", false, details);
      if (ok) await startJob("/sync", { ids: [d.id], from }, `${to}로 동기화`);
      return;
    }
    if (act === "push") {
      const from = btn.dataset.from, branch = btn.dataset.branch, force = btn.dataset.force === "1";
      const to = from === "github" ? "GitLab" : "GitHub";
      if (force) {
        const b = d.branches.find((x) => x.name === branch);
        const lost = from === "github" ? b.gitLabAhead : b.gitHubAhead;
        const ok = await confirm("강제 푸시", `${to}의 '${branch}' 브랜치를 덮어씁니다.`, "강제 푸시", true,
          [`${to}에만 있는 커밋 ${lost}개가 브랜치에서 사라집니다.`, "보호된 브랜치는 서버에서 거부될 수 있습니다."]);
        if (!ok) return;
      }
      await startJob("/push", { id: d.id, branch, from, force }, `'${branch}' → ${to} 푸시`);
    }
  }

  // ───────────────────────── modal / toast ─────────────────────────

  function openModal({ title, message, ok = "확인", danger = false, details = [], input = false }) {
    return new Promise((resolve) => {
      const m = $("#modal");
      $("#dlgTitle").textContent = title;
      $("#dlgMsg").textContent = message;
      $("#dlgDetails").innerHTML = details.map((d) => `<div>${esc(d)}</div>`).join("");
      $("#dlgIcon").className = "dialog-icon" + (danger ? " danger" : "");
      $("#dlgIcon").innerHTML = icon(danger ? "warn" : input ? "key" : "sync");
      $("#dlgOk").textContent = ok;
      $("#dlgOk").className = "btn " + (danger ? "danger" : "primary");
      $("#dlgInputWrap").hidden = !input;
      $("#dlgInput").value = "";
      m.hidden = false;
      (input ? $("#dlgInput") : $("#dlgOk")).focus();
      const close = (v) => { m.hidden = true; document.removeEventListener("keydown", onKey); resolve(v); };
      const onKey = (e) => { if (e.key === "Escape") close(null); if (e.key === "Enter") close(input ? $("#dlgInput").value : true); };
      document.addEventListener("keydown", onKey);
      $("#dlgOk").onclick = () => close(input ? $("#dlgInput").value : true);
      $("#dlgCancel").onclick = () => close(null);
    });
  }
  const confirm = (title, message, ok, danger, details) => openModal({ title, message, ok, danger, details }).then((v) => !!v);
  const prompt = (title, message) => openModal({ title, message, ok: "확인", input: true });

  function toast(message, kind = "Info") {
    const el = document.createElement("div");
    el.className = "toast t-" + kind;
    el.innerHTML = `<div class="ti">${icon(LOG_ICON[kind] || "info")}</div><div class="tm">${esc(message)}</div><button title="닫기">${icon("x")}</button>`;
    el.querySelector("button").onclick = () => el.remove();
    $("#toasts").appendChild(el);
    setTimeout(() => el.remove(), 4500);
    while ($("#toasts").children.length > 4) $("#toasts").firstChild.remove();
  }

  // ───────────────────────── helpers ─────────────────────────

  function rel(iso) {
    if (!iso) return "";
    const s = (Date.now() - new Date(iso).getTime()) / 1000;
    if (s < 60) return "방금 전";
    if (s < 3600) return `${Math.floor(s / 60)}분 전`;
    if (s < 86400) return `${Math.floor(s / 3600)}시간 전`;
    if (s < 86400 * 30) return `${Math.floor(s / 86400)}일 전`;
    return new Date(iso).toLocaleDateString("ko-KR");
  }
  const fmtTime = (iso) => {
    const d = new Date(iso), p = (n) => String(n).padStart(2, "0");
    return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`;
  };

  function go(page) {
    state.page = page;
    $$(".nav-item").forEach((b) => b.classList.toggle("active", b.dataset.page === page));
    $$(".page").forEach((p) => (p.hidden = p.id !== "page-" + page));
    if (page === "settings") loadSettings();
    if (page === "logs") renderLogs();
  }

  // ───────────────────────── wiring ─────────────────────────

  $$(".nav-item").forEach((b) => b.addEventListener("click", () => go(b.dataset.page)));
  document.addEventListener("click", (e) => {
    const a = e.target.closest("[data-action]");
    if (a && !a.disabled) { $("#moreMenu").hidden = true; onAction(a.dataset.action); return; }
    const d = e.target.closest(".detail [data-act]");
    if (d && !d.disabled) { onDetailAction(d); return; }
    if (!e.target.closest(".menu-wrap")) $("#moreMenu").hidden = true;
  });
  $("#moreBtn").addEventListener("click", () => ($("#moreMenu").hidden = !$("#moreMenu").hidden));
  $$(".stat").forEach((b) => b.addEventListener("click", () => {
    state.filter = b.dataset.filter;
    $$(".stat").forEach((x) => x.classList.toggle("active", x === b));
    renderList();
  }));
  $("#search").addEventListener("input", (e) => { state.search = e.target.value; renderList(); });
  $("#repoList").addEventListener("click", (e) => {
    const r = e.target.closest(".repo");
    if (!r) return;
    state.selectedId = r.dataset.id;
    safeSet("gitsync.selected", state.selectedId);
    renderList();
    loadDetail(state.selectedId);
  });
  $("#cancelJob").addEventListener("click", () => post("/cancel").then(() => toast("취소를 요청했습니다.", "Warning")));
  $("#clearLogs").addEventListener("click", () => { state.logs = []; renderLogs(); });
  $("#saveSettings").addEventListener("click", saveSettings);
  $("#settingsForm").addEventListener("input", () => setDirty(true));
  $("#settingsForm").addEventListener("submit", (e) => e.preventDefault());
  $$("[data-test]").forEach((b) => b.addEventListener("click", () => testConnection(b.dataset.test)));
  setInterval(() => { if (state.page === "repos" && state.detail) renderDetail(); }, 60000);

  (async () => {
    await refreshStatus();
    await loadRepos(true);
    await loadSettings();
    renderDetail();
    if (state.status && !state.status.configured) { go("settings"); toast("먼저 GitHub와 GitLab 토큰을 설정하세요.", "Warning"); }
    poll();
  })();
})();
