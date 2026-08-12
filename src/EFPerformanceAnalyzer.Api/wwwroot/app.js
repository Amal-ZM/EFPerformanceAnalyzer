"use strict";

// ---------------------------------------------------------------------------
// Category metadata. The API returns a bare enum name like "CartesianInclude";
// a developer meeting that term for the first time needs a plain-English label
// and a one-line reason it costs them something.
// ---------------------------------------------------------------------------
const CATEGORIES = {
  NPlusOneQuery:            ["N+1 query", "One extra query fires per loop iteration."],
  MissingAsNoTracking:      ["Missing AsNoTracking", "Read-only query still pays change-tracking overhead."],
  MissingInclude:           ["Missing Include", "Navigation dereferenced without eager loading."],
  UnusedNavigationProperty: ["Unused navigation", "A mapped relationship nothing ever reads."],
  MultipleSaveChanges:      ["Multiple SaveChanges", "More database round trips than the method needs."],
  ClientSideEvaluation:     ["Client-side evaluation", "Whole table loaded, then filtered in memory."],
  QueryInLoop:              ["Query inside a loop", "A fresh round trip on every iteration."],
  SaveChangesInLoop:        ["SaveChanges in a loop", "One transaction per iteration instead of one batch."],
  UnboundedQuery:           ["Unbounded query", "No filter and no paging — cost grows with the table."],
  CartesianInclude:         ["Cartesian Include", "Stacked Includes multiply the rows returned."],
  InefficientCount:         ["Count used as exists", "COUNT(*) where EXISTS would stop at the first row."],
  SyncOverAsync:            ["Blocking on async", "Holds a thread-pool thread; can deadlock."],
  AsyncVoid:                ["async void", "Caller can't await it or catch its exceptions."],
  StringConcatInLoop:       ["String += in a loop", "Reallocates and copies the whole string each pass."],
  BlockingCallInAsyncMethod:["Blocking call in async", "Thread.Sleep parks the thread instead of yielding it."]
};

const SEVERITY_WEIGHT = { Critical: 10, Warning: 3, Info: 1 };

let currentFindings = [];
let currentSummary = null;

const $ = (id) => document.getElementById(id);
const escapeHtml = (s) => String(s ?? "").replace(/[&<>"']/g,
  (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------------------------------------------------------------------------
// Tabs
// ---------------------------------------------------------------------------
const TABS = [
  ["tabPath", "panePath"],
  ["tabFolder", "paneFolder"],
  ["tabUpload", "paneUpload"],
  ["tabHistory", "paneHistory"]
];
for (const [tabId, paneId] of TABS) {
  $(tabId).addEventListener("click", () => {
    for (const [t, p] of TABS) {
      const active = t === tabId;
      $(t).classList.toggle("active", active);
      $(t).setAttribute("aria-selected", String(active));
      $(p).classList.toggle("hidden", !active);
    }
    if (tabId === "tabHistory") loadRuns();
  });
}

// ---------------------------------------------------------------------------
// Status line
// ---------------------------------------------------------------------------
function setStatus(text, kind) {
  const el = $("status");
  if (!text) { el.classList.add("hidden"); return; }
  el.className = "status " + (kind || "working");
  el.textContent = text;
}

async function readError(res) {
  try {
    const body = await res.json();
    return body.error || body.title || `Request failed (${res.status})`;
  } catch {
    return `Request failed (${res.status})`;
  }
}

// ---------------------------------------------------------------------------
// Scanning
// ---------------------------------------------------------------------------
async function scanPath() {
  const targetPath = $("targetPath").value.trim();
  if (!targetPath) { setStatus("Enter a project path first.", "error"); return; }

  $("scanBtn").disabled = true;
  setStatus(`Analyzing ${targetPath}…`, "working");
  try {
    const res = await fetch("/api/analysis/scans", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targetPath })
    });
    if (!res.ok) throw new Error(await readError(res));
    await showRun((await res.json()).runId);
  } catch (err) {
    setStatus(err.message, "error");
  } finally {
    $("scanBtn").disabled = false;
  }
}

async function scanUpload() {
  const file = $("zipFile").files[0];
  if (!file) { setStatus("Choose a .zip file first.", "error"); return; }

  $("uploadBtn").disabled = true;
  setStatus(`Uploading and analyzing ${file.name}…`, "working");
  try {
    const form = new FormData();
    form.append("file", file);
    const res = await fetch("/api/analysis/scans/upload", { method: "POST", body: form });
    if (!res.ok) throw new Error(await readError(res));
    await showRun((await res.json()).runId);
  } catch (err) {
    setStatus(err.message, "error");
  } finally {
    $("uploadBtn").disabled = false;
  }
}

// ---------------------------------------------------------------------------
// Folder upload — a browser-picked or dragged-in folder, sent as individual
// files (each carrying its relative path) rather than a zip.
// ---------------------------------------------------------------------------
const EXCLUDED_DIR_SEGMENTS = new Set(["bin", "obj", "node_modules", ".git", ".vs"]);
let pendingFolderFiles = []; // [{ file, relativePath }]

function isWantedCsFile(relativePath) {
  if (!relativePath.toLowerCase().endsWith(".cs")) return false;
  return !relativePath.split(/[\\/]/).some((seg) => EXCLUDED_DIR_SEGMENTS.has(seg));
}

function collectFromFileInput(fileList) {
  const out = [];
  for (const file of fileList) {
    const rel = file.webkitRelativePath || file.name;
    if (isWantedCsFile(rel)) out.push({ file, relativePath: rel });
  }
  return out;
}

// Drag-and-drop gives FileSystemEntry objects, not a flat FileList, so a dropped
// folder has to be walked by hand to rebuild each file's relative path.
function readDirectoryEntries(reader) {
  return new Promise((resolve, reject) => {
    const all = [];
    const readBatch = () => {
      reader.readEntries((entries) => {
        if (!entries.length) { resolve(all); return; }
        all.push(...entries);
        readBatch();
      }, reject);
    };
    readBatch();
  });
}

async function walkEntry(entry, prefix, out) {
  if (entry.isFile) {
    const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
    const rel = prefix + entry.name;
    if (isWantedCsFile(rel)) out.push({ file, relativePath: rel });
  } else if (entry.isDirectory) {
    if (EXCLUDED_DIR_SEGMENTS.has(entry.name)) return;
    const entries = await readDirectoryEntries(entry.createReader());
    for (const child of entries) await walkEntry(child, prefix + entry.name + "/", out);
  }
}

async function collectFromDataTransfer(dataTransfer) {
  const items = [...dataTransfer.items];
  const entries = items.map((item) => item.webkitGetAsEntry?.()).filter(Boolean);
  if (!entries.length) return collectFromFileInput(dataTransfer.files); // fallback: plain file drop

  const out = [];
  for (const entry of entries) await walkEntry(entry, "", out);
  return out;
}

function folderNameFromCollection(collected) {
  const first = collected[0]?.relativePath || "";
  return first.includes("/") ? first.split("/")[0] : "uploaded-project";
}

function showFolderSummary() {
  const summary = $("folderSummary");
  if (!pendingFolderFiles.length) { summary.classList.add("hidden"); return; }
  const name = folderNameFromCollection(pendingFolderFiles);
  $("folderSummaryText").textContent =
    `${pendingFolderFiles.length} .cs file(s) ready — "${name}"`;
  summary.classList.remove("hidden");
}

function setUploadProgress(pct) {
  const wrap = $("uploadProgress");
  if (pct == null) { wrap.classList.add("hidden"); return; }
  wrap.classList.remove("hidden");
  $("uploadProgressBar").style.width = `${pct}%`;
}

function scanFolderUpload() {
  if (!pendingFolderFiles.length) { setStatus("Choose or drop a project folder first.", "error"); return; }

  const folderName = folderNameFromCollection(pendingFolderFiles);
  const form = new FormData();
  for (const { file, relativePath } of pendingFolderFiles) form.append("files", file, relativePath);
  form.append("folderName", folderName);

  $("folderScanBtn").disabled = true;
  setStatus(`Uploading ${pendingFolderFiles.length} file(s) from "${folderName}"…`, "working");
  setUploadProgress(0);

  const xhr = new XMLHttpRequest();
  xhr.open("POST", "/api/analysis/scans/upload-folder");
  xhr.upload.addEventListener("progress", (e) => {
    if (e.lengthComputable) setUploadProgress(Math.round((e.loaded / e.total) * 100));
  });
  xhr.addEventListener("load", async () => {
    setUploadProgress(null);
    $("folderScanBtn").disabled = false;
    if (xhr.status < 200 || xhr.status >= 300) {
      let msg = `Request failed (${xhr.status})`;
      try { msg = JSON.parse(xhr.responseText).error || msg; } catch { /* keep default */ }
      setStatus(msg, "error");
      return;
    }
    setStatus("Analyzing…", "working");
    try {
      const { runId } = JSON.parse(xhr.responseText);
      await showRun(runId);
    } catch (err) {
      setStatus(err.message, "error");
    }
  });
  xhr.addEventListener("error", () => {
    setUploadProgress(null);
    $("folderScanBtn").disabled = false;
    setStatus("Upload failed — network error.", "error");
  });
  xhr.send(form);
}

function setupFolderDropzone() {
  const zone = $("dropzone");
  const input = $("folderInput");

  zone.addEventListener("click", () => input.click());
  zone.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); input.click(); } });

  input.addEventListener("change", () => {
    pendingFolderFiles = collectFromFileInput(input.files);
    showFolderSummary();
  });

  ["dragenter", "dragover"].forEach((evt) =>
    zone.addEventListener(evt, (e) => { e.preventDefault(); zone.classList.add("dragover"); }));
  ["dragleave", "drop"].forEach((evt) =>
    zone.addEventListener(evt, (e) => { e.preventDefault(); zone.classList.remove("dragover"); }));

  zone.addEventListener("drop", async (e) => {
    setStatus("Reading dropped folder…", "working");
    try {
      pendingFolderFiles = await collectFromDataTransfer(e.dataTransfer);
      showFolderSummary();
      setStatus("", null);
    } catch {
      setStatus("Could not read the dropped folder — try the browse button instead.", "error");
    }
  });

  $("folderClearBtn").addEventListener("click", () => {
    pendingFolderFiles = [];
    input.value = "";
    showFolderSummary();
  });

  $("folderScanBtn").addEventListener("click", scanFolderUpload);
}

async function loadRuns() {
  const select = $("runSelect");
  try {
    const res = await fetch("/api/analysis/runs");
    const runs = await res.json();
    if (!runs.length) {
      select.innerHTML = "<option value=''>No scans yet</option>";
      $("baselineSelect").innerHTML = $("currentSelect").innerHTML = "<option value=''>No scans yet</option>";
      $("trendChart").innerHTML = "";
      return;
    }
    const optionsHtml = runs.map((r) =>
      `<option value="${r.runId}">#${r.runId} · ${escapeHtml(r.targetPath)} · ${r.totalFindings} findings</option>`
    ).join("");
    select.innerHTML = optionsHtml;
    $("baselineSelect").innerHTML = optionsHtml;
    $("currentSelect").innerHTML = optionsHtml;
    // Sensible defaults: compare the two most recent runs.
    if (runs.length > 1) $("baselineSelect").value = runs[1].runId;
    renderTrendChart(runs);
  } catch {
    select.innerHTML = "<option value=''>Could not load history</option>";
  }
}

// ---------------------------------------------------------------------------
// Trend chart — total findings per run, most recent last. Uses data already
// fetched for the history dropdown, no extra request.
// ---------------------------------------------------------------------------
function renderTrendChart(runs) {
  const el = $("trendChart");
  const chronological = [...runs].reverse().slice(-24); // oldest → newest, cap at 24 bars
  if (chronological.length < 2) { el.innerHTML = ""; return; }

  const max = Math.max(...chronological.map((r) => r.totalFindings), 1);
  const w = 640, h = 90, padTop = 10, barGap = 3;
  const barW = (w / chronological.length) - barGap;

  const bars = chronological.map((r, i) => {
    const barH = Math.max(2, (r.totalFindings / max) * (h - padTop));
    const x = i * (barW + barGap);
    const y = h - barH;
    const title = `Run #${r.runId} · ${new Date(r.startedAtUtc).toLocaleDateString()} · ${r.totalFindings} finding(s)`;
    return `<rect x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${barW.toFixed(1)}" height="${barH.toFixed(1)}" rx="2"><title>${escapeHtml(title)}</title></rect>`;
  }).join("");

  const first = chronological[0], last = chronological[chronological.length - 1];
  el.innerHTML = `
    <div class="trend-head">
      <span class="sub" style="margin:0">Findings per run, oldest → newest (${chronological.length} of ${runs.length})</span>
    </div>
    <svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" class="trend-svg">${bars}</svg>
    <div class="trend-foot">
      <span>${new Date(first.startedAtUtc).toLocaleDateString()} · ${first.totalFindings}</span>
      <span>${new Date(last.startedAtUtc).toLocaleDateString()} · ${last.totalFindings}</span>
    </div>`;
}

// ---------------------------------------------------------------------------
// Baseline/current diff
// ---------------------------------------------------------------------------
async function compareRuns() {
  const baselineId = $("baselineSelect").value;
  const currentId = $("currentSelect").value;
  const out = $("diffResult");

  if (!baselineId || !currentId) { out.innerHTML = "<p class='hint'>Pick two runs first.</p>"; return; }
  if (baselineId === currentId) { out.innerHTML = "<p class='hint'>Pick two different runs.</p>"; return; }

  out.innerHTML = "<p class='hint'>Comparing…</p>";
  try {
    const res = await fetch(`/api/analysis/runs/${baselineId}/diff/${currentId}`);
    if (!res.ok) throw new Error(await readError(res));
    const diff = await res.json();
    renderDiff(diff);
  } catch (err) {
    out.innerHTML = `<p class='hint'>${escapeHtml(err.message)}</p>`;
  }
}

function renderDiff(diff) {
  const out = $("diffResult");
  const section = (title, cls, items, emptyMsg) => `
    <div class="diff-section ${cls}">
      <h3>${title} (${items.length})</h3>
      ${items.length ? items.map((f) =>
        `<div class="diff-item">
          <span class="badge ${f.severity.toLowerCase()}">${escapeHtml(f.severity)}</span>
          <span class="cat">${escapeHtml((CATEGORIES[f.category] || [f.category])[0])}</span>
          <span class="path mono">${escapeHtml(f.filePath)}:${f.line}</span>
        </div>`).join("") : `<p class="hint">${emptyMsg}</p>`}
    </div>`;

  out.innerHTML = `
    <p class="sub" style="margin:10px 0">
      #${diff.baselineRunId} &rarr; #${diff.currentRunId} &nbsp;·&nbsp; ${diff.persistingCount} unchanged
    </p>
    <div class="diff-grid">
      ${section("New", "new", diff.newFindings, "Nothing new — good sign.")}
      ${section("Resolved", "resolved", diff.resolvedFindings, "Nothing resolved between these two runs.")}
    </div>`;
}

async function showRun(runId) {
  setStatus("Loading findings…", "working");
  const res = await fetch(`/api/analysis/runs/${runId}`);
  if (!res.ok) throw new Error(await readError(res));

  const detail = await res.json();
  currentSummary = detail.summary;
  currentFindings = detail.findings;

  buildCategoryFilter();
  renderStats();
  renderHotspots();
  renderFindings();

  for (const [id, fmt] of [["exportSarif", "sarif"], ["exportCsv", "csv"], ["exportMd", "md"]]) {
    $(id).href = `/api/analysis/runs/${runId}/export/${fmt}`;
  }

  $("results").classList.remove("hidden");
  const took = new Date(currentSummary.completedAtUtc) - new Date(currentSummary.startedAtUtc);
  setStatus(
    `Scanned ${currentSummary.filesScanned} file(s) in ${(took / 1000).toFixed(1)}s — ` +
    `${currentSummary.totalFindings} finding(s).`, "ok");
}

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------
function countBy(severity) {
  return currentFindings.filter((f) => f.severity === severity).length;
}

function renderStats() {
  const s = currentSummary;
  const stats = [
    { n: countBy("Critical"), l: "Critical", cls: "critical" },
    { n: countBy("Warning"),  l: "Warning",  cls: "warning" },
    { n: countBy("Info"),     l: "Info",     cls: "" },
    { n: s.filesScanned,      l: "Files scanned", cls: "" },
    { n: s.dbContextsFound,   l: "DbContexts", cls: "" },
    { n: s.entityTypesFound,  l: "Entity types", cls: "" }
  ];
  if (s.suppressedCount > 0) stats.push({ n: s.suppressedCount, l: "Suppressed", cls: "" });
  $("statgrid").innerHTML = stats.map((x) =>
    `<div class="stat ${x.cls}"><div class="n">${x.n}</div><div class="l">${x.l}</div></div>`
  ).join("");
}

function fileScore(findings) {
  return findings.reduce((sum, f) => sum + (SEVERITY_WEIGHT[f.severity] || 1), 0);
}

function renderHotspots() {
  const byFile = new Map();
  for (const f of currentFindings) {
    if (!byFile.has(f.filePath)) byFile.set(f.filePath, []);
    byFile.get(f.filePath).push(f);
  }

  const ranked = [...byFile.entries()]
    .map(([path, fs]) => ({ path, count: fs.length, score: fileScore(fs) }))
    .sort((a, b) => b.score - a.score)
    .slice(0, 12);

  if (!ranked.length) {
    $("hotspotList").innerHTML = "<li class='sub'>Nothing found.</li>";
    return;
  }

  $("hotspotList").innerHTML = ranked.map((r) => {
    const name = r.path.split(/[\\/]/).pop();
    return `<li><button data-file="${escapeHtml(r.path)}" title="${escapeHtml(r.path)}">
      <span class="fname">${escapeHtml(name)}</span>
      <span class="cnt">${r.count}</span>
    </button></li>`;
  }).join("");

  for (const btn of $("hotspotList").querySelectorAll("button")) {
    btn.addEventListener("click", () => {
      $("textFilter").value = btn.dataset.file;
      renderFindings();
    });
  }
}

function buildCategoryFilter() {
  const present = [...new Set(currentFindings.map((f) => f.category))].sort();
  $("categoryFilter").innerHTML = "<option value=''>All categories</option>" +
    present.map((c) => {
      const n = currentFindings.filter((f) => f.category === c).length;
      const label = (CATEGORIES[c] || [c])[0];
      return `<option value="${c}">${escapeHtml(label)} (${n})</option>`;
    }).join("");
}

function activeSeverities() {
  return [...document.querySelectorAll(".sev:checked")].map((c) => c.value);
}

function renderFindings() {
  const severities = activeSeverities();
  const category = $("categoryFilter").value;
  const text = $("textFilter").value.trim().toLowerCase();

  const order = { Critical: 0, Warning: 1, Info: 2 };
  const visible = currentFindings
    .filter((f) => severities.includes(f.severity))
    .filter((f) => !category || f.category === category)
    .filter((f) => !text ||
      f.filePath.toLowerCase().includes(text) ||
      f.memberName.toLowerCase().includes(text))
    .sort((a, b) => (order[a.severity] - order[b.severity]) ||
                    a.filePath.localeCompare(b.filePath) ||
                    a.line - b.line);

  $("findingCount").textContent =
    `Showing ${visible.length} of ${currentFindings.length} finding(s), most severe first.`;

  if (!visible.length) {
    $("findingList").innerHTML = "<div class='empty'>No findings match these filters.</div>";
    return;
  }

  $("findingList").innerHTML = visible.map(renderFinding).join("");
  wireFindingButtons();
}

function renderFinding(f) {
  const sev = f.severity.toLowerCase();
  const [label, blurb] = CATEGORIES[f.category] || [f.category, ""];
  const isAbsolute = /^[a-zA-Z]:[\\/]/.test(f.filePath);
  const loc = `${f.filePath}:${f.line}`;

  const openBtn = isAbsolute
    ? `<a class="mini" href="vscode://file/${encodeURI(f.filePath.replace(/\\/g, "/"))}:${f.line}"
          title="Open in VS Code">Open in VS Code</a>`
    : "";

  return `
    <article class="finding ${sev}">
      <div class="f-head">
        <span class="badge ${sev}">${escapeHtml(f.severity)}</span>
        <span class="cat">${escapeHtml(label)}</span>
        <span class="sub" style="margin:0">${escapeHtml(blurb)}</span>
      </div>
      <div class="loc">
        <span class="path">${escapeHtml(loc)}</span>
        <span class="member">${escapeHtml(f.memberName)}</span>
        <button class="mini copy" data-copy="${escapeHtml(loc)}">Copy path</button>
        ${openBtn}
      </div>
      <pre><code>${escapeHtml(f.codeSnippet)}</code></pre>
      <p class="msg">${escapeHtml(f.message)}</p>
      ${f.recommendation ? `<p class="rec"><strong>Fix:</strong> ${escapeHtml(f.recommendation)}</p>` : ""}
    </article>`;
}

function wireFindingButtons() {
  for (const btn of document.querySelectorAll(".copy")) {
    btn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(btn.dataset.copy);
        const original = btn.textContent;
        btn.textContent = "Copied";
        setTimeout(() => { btn.textContent = original; }, 1200);
      } catch {
        btn.textContent = "Copy failed";
      }
    });
  }
}

// ---------------------------------------------------------------------------
// Wiring
// ---------------------------------------------------------------------------
$("scanBtn").addEventListener("click", scanPath);
$("uploadBtn").addEventListener("click", scanUpload);
setupFolderDropzone();
$("targetPath").addEventListener("keydown", (e) => { if (e.key === "Enter") scanPath(); });
$("loadRunBtn").addEventListener("click", async () => {
  const runId = $("runSelect").value;
  if (!runId) return;
  try { await showRun(runId); } catch (err) { setStatus(err.message, "error"); }
});
$("categoryFilter").addEventListener("change", renderFindings);
$("textFilter").addEventListener("input", renderFindings);
for (const chk of document.querySelectorAll(".sev")) chk.addEventListener("change", renderFindings);
$("compareBtn").addEventListener("click", compareRuns);

// Offer the sample project as a one-click starting point so a first run needs no typing.
(function seedSuggestions() {
  const samples = [
    "C:\\TechProcess\\data base\\EFPerformanceAnalyzer\\samples\\SampleTarget",
    "C:\\TechProcess\\data base\\SchoolFinder"
  ];
  $("suggestions").innerHTML = samples
    .map((p) => `<button data-path="${escapeHtml(p)}">${escapeHtml(p.split("\\").pop())}</button>`)
    .join("");
  for (const btn of $("suggestions").querySelectorAll("button")) {
    btn.addEventListener("click", () => { $("targetPath").value = btn.dataset.path; scanPath(); });
  }
})();
