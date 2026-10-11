/* Read-only saved-run browser. Every request is GET and every render is scoped to its run/source. */
(() => {
  "use strict";
  const $ = (id) => document.getElementById(id);
  const state = { run: null, generation: 0, catalog: new Map(), catalogCursor: null, catalogCursorInitialized: false, manifest: null,
    records: [], recordPin: null, recordAfter: 0, recordMore: false, recordScope: null,
    checkpoints: [], checkpointAfter: 0, checkpointSyncRevision: 0, checkpointHasMore: false, destroyed: false, report: null, datasets: null, dataset: null,
    datasetCursor: null, datasetMore: false, datasetRowsShown: 0, channels: new Map(), discovering: false };
  const statusLabels = {
    available: "Available", partial: "Partially captured", not_captured: "Not captured",
    unavailable: "Unavailable", unknown: "Unknown / no evidence", awaiting_finished_run: "Awaiting a finished run",
    not_published: "No published import evidence", report_only: "Report available; row cache unavailable",
    stale: "Publication is older than the saved run", normal: "Normal timeline", fork_unsupported: "Fork timeline · not supported",
    running: "Running", completed: "Completed", stopped: "Stopped"
  };
  const label = (value) => statusLabels[value] || (value ? String(value).replaceAll("_", " ") : "Unknown / no evidence");
  const json = (value) => JSON.stringify(value, null, 2);
  const show = (id, text = "", error = false) => { $(id).textContent = text; $(id).classList.toggle("error", error); };
  const number = (value) => value === null || value === undefined ? "Unknown" : Number(value).toLocaleString();
  const endpoint = (suffix = "") => `/api/runs/${encodeURIComponent(state.run)}${suffix}`;
  const isCurrent = (token) => state.run === token.run && state.generation === token.generation && state.channels.get(token.channel) === token;
  function cancel(channel) {
    const old = state.channels.get(channel);
    old?.controller.abort();
    state.channels.delete(channel);
  }
  function begin(channel) {
    cancel(channel);
    const token = { channel, run: state.run, generation: state.generation, controller: new AbortController() };
    state.channels.set(channel, token);
    return token;
  }
  async function get(path, token) {
    const response = await fetch(path, { method: "GET", signal: token.controller.signal, headers: { Accept: "application/json" } });
    if (!response.ok) {
      const error = new Error(response.status === 409 ? "Saved source changed or its pin is no longer available. Refresh this run before continuing."
        : response.status === 404 ? "No saved data is available at this source."
        : `Could not read saved data (HTTP ${response.status}).`);
      error.status = response.status; throw error;
    }
    return response.json();
  }
  async function request(channel, path, render, fail) {
    const token = begin(channel);
    try { const value = await get(path, token); if (isCurrent(token)) render(value); }
    catch (error) { if (isCurrent(token) && error.name !== "AbortError") fail?.(error); }
    finally { if (isCurrent(token)) state.channels.delete(channel); }
  }
  function appendOption(select, value, text) {
    const option = document.createElement("option"); option.value = value; option.textContent = text; select.append(option); return option;
  }
  function details(value, title) {
    const item = document.createElement("details"), summary = document.createElement("summary"), pre = document.createElement("pre");
    summary.textContent = title; pre.textContent = json(value); item.append(summary, pre); return item;
  }
  function cell(row, text, className) {
    const item = document.createElement("td"); item.textContent = text; if (className) item.className = className; row.append(item);
  }
  function renderCatalog() {
    const select = $("data-runs"); select.replaceChildren();
    if (state.run && !state.catalog.has(state.run)) appendOption(select, state.run, `${state.run} · selected run`);
    for (const run of [...state.catalog.values()].sort((a,b) => b.run_id.localeCompare(a.run_id)))
      appendOption(select, run.run_id, `${run.run_id} · ${label(run.status)}`);
    if (!select.options.length) appendOption(select, "", "No saved simulations found");
    if (state.run) select.value = state.run;
  }
  function renderSource(storage) {
    const local = storage === "local", neon = storage === "neon_https";
    show("source-title", local ? "Local saved data · not connected to Neon" : neon ? "Connected to Neon saved data" : "Saved data source not identified");
    show("source-description", local
      ? "This page lists runs saved on this computer. It does not list your production Neon runs or count all runs in Databricks."
      : neon ? "The simulation list comes from this server's configured Neon branch. Published Databricks results are shown separately for each run."
      : "The simulation list is not a count of all runs in Databricks. Check the selected run's storage and publication status below.");
  }
  async function discover(older = false) {
    if (state.discovering) return;
    state.discovering = true; $("more-runs").disabled = true;
    const token = begin("catalog"), query = new URLSearchParams({ limit: "50" });
    if (older && state.catalogCursor) query.set("cursor", state.catalogCursor);
    try {
      const result = await get(`/api/data/runs?${query}`, token);
      if (!isCurrent(token)) return;
      if (!Array.isArray(result.runs)) throw new Error("Run discovery returned an invalid catalog.");
      for (const run of result.runs) state.catalog.set(run.run_id, run);
      // Refreshing the first page must not rewind an older-page cursor.
      if (older || !state.catalogCursorInitialized) { state.catalogCursor = result.next_cursor ?? null; state.catalogCursorInitialized = true; }
      $("more-runs").hidden = !state.catalogCursor;
      renderSource(result.storage ?? state.manifest?.storage); renderCatalog(); show("discovery-status", `${number(state.catalog.size)} ${result.storage === "local" ? "local " : ""}saved simulations found`);
      if (!state.run && result.runs.length) selectRun(result.runs[0].run_id);
    } catch (error) {
      if (isCurrent(token) && error.name !== "AbortError") show("discovery-status", `Run discovery unavailable. ${error.message}`, true);
    } finally {
      state.discovering = false; if (!state.destroyed) $("more-runs").disabled = false;
      if (isCurrent(token)) state.channels.delete("catalog");
    }
  }
  function resetRunViews() {
    state.manifest = null; state.recordPin = null; state.recordScope = null; state.records = []; state.recordAfter = 0;
    state.checkpoints = []; state.checkpointAfter = 0; state.checkpointSyncRevision = 0; state.checkpointHasMore = false; state.report = null; state.datasets = null; state.dataset = null;
    state.datasetCursor = null; state.datasetRowsShown = 0;
    for (const id of ["run-facts", "run-duration", "capture-metadata", "checkpoints", "frame-json", "records", "page-fields", "report-json", "dataset-columns", "dataset-fields", "dataset-rows", "dataset-catalog-json"]) $(id).replaceChildren();
    for (const id of ["more-records", "more-checkpoints", "more-dataset-rows", "frame-details", "report-download"]) $(id).hidden = true;
    for (const id of ["report-section", "dataset-select"]) { $(id).replaceChildren(); appendOption($(id), "", "No data available"); $(id).disabled = true; }
    show("run-status", "Reading committed saved data…"); show("records-status", "Waiting for capture information.");
    show("metadata-status", "Unknown until saved metadata is read."); show("records-source", "No source selected");
    show("checkpoints-status", "Loading…"); show("report-status", "Waiting for publication information."); show("report-preview");
    show("datasets-status", "Waiting for publication information."); show("datasets-source", "No published row cache");
    show("dataset-status"); show("dataset-rows-status"); show("dataset-coverage-scope"); renderCoverage();
  }
  function selectRun(run) {
    if (!run) return;
    state.generation++;
    for (const channel of [...state.channels.keys()]) cancel(channel);
    state.run = run; resetRunViews(); renderCatalog();
    const url = new URL(location.href); url.searchParams.set("run_id", run); history.replaceState(null, "", url);
    $("observer-link").href = `/?run_id=${encodeURIComponent(run)}`;
    loadManifest(true);
  }
  function renderManifest(manifest) {
    state.manifest = manifest;
    renderSource(manifest.storage);
    const day = manifest.tick;
    show("run-duration", Number.isSafeInteger(day)
      ? `Selected run reached day ${number(day)}: ${number(Math.floor(day / 7))} full weeks${day % 7 ? ` plus ${number(day % 7)} day${day % 7 === 1 ? "" : "s"}` : ""}. A 365-day run has 52 full weeks plus 1 day; this page shows only history actually saved for the selected run.`
      : "This run's saved duration is unknown. Weeks are shown only where saved history exists.");
    show("run-status", `${label(manifest.status)} · committed revision ${number(manifest.through_revision)} · Timeline: ${label(manifest.timeline_status)}`);
    const facts = [ ["Saved day", number(manifest.tick)], ["Original records", label(manifest.audit_status)],
      ["Databricks publication", label(manifest.databricks_status)], ["Storage", manifest.storage === "neon_https" ? "Neon" : manifest.storage === "local" ? "Local saved storage" : label(manifest.storage)],
      ["Daily capture through", number(manifest.audit_daily_through_tick)], ["Opening balances", manifest.audit_opening_balances_captured === true ? "Captured" : manifest.audit_opening_balances_captured === false ? "Not captured" : "Unknown"] ];
    $("run-facts").replaceChildren();
    for (const [name, value] of facts) { const box = document.createElement("div"), dt = document.createElement("dt"), dd = document.createElement("dd"); dt.textContent = name; dd.textContent = value; box.append(dt,dd); $("run-facts").append(box); }
    $("capture-metadata").textContent = manifest.capture_metadata == null ? "No original capture metadata available." : json(manifest.capture_metadata);
    show("metadata-status", manifest.capture_metadata == null ? (manifest.capture_metadata_reason || "Legacy / unknown. Missing settings do not mean a feature was disabled.") : "Original run metadata; no current simulation defaults added.");
    show("records-source", manifest.storage === "neon_https" ? "Committed Neon records" : "Committed saved records");
    const periods = $("record-period"), selected = periods.value;
    for (const option of [...periods.options]) if (option.value.startsWith("week:")) option.remove();
    const lastDay = manifest.audit_daily_through_tick;
    if (Number.isSafeInteger(lastDay) && lastDay > 0) for (let week = 1; week <= Math.ceil(lastDay / 7); week++)
      appendOption(periods, `week:${week}`, `Week ${week} · days ${(week - 1) * 7 + 1}–${Math.min(week * 7, lastDay)}${week * 7 > lastDay ? " · partial" : ""}`);
    if ([...periods.options].some(option => option.value === selected)) periods.value = selected;
    renderCoverage();
  }
  function publicationKey(manifest) {
    return json([manifest.databricks_status, manifest.dataset_catalog?.bundle_manifest_sha256, manifest.dataset_catalog?.source_manifest_sha256, manifest.dataset_catalog?.generated_at]);
  }
  function loadManifest(initial = false) {
    if (!initial && state.channels.has("manifest")) return;
    return request("manifest", endpoint("/data/manifest"), (manifest) => {
      if (manifest.run_id !== state.run) throw new Error("Saved manifest belongs to another simulation.");
      const previous = state.manifest, publicationChanged = previous && publicationKey(previous) !== publicationKey(manifest);
      renderManifest(manifest);
      if (initial) { loadCheckpoints(); loadRecords(true); loadPublications(); }
      else {
        if (publicationChanged) loadPublications();
        if (manifest.through_revision > state.checkpointSyncRevision) loadCheckpoints(false, true);
        if (state.recordPin && manifest.through_sequence > state.recordPin.through_sequence)
          show("records-source", `${manifest.storage === "neon_https" ? "Neon" : "Saved storage"} · viewing revision ${state.recordPin.through_revision}; newer saved records available`);
      }
    }, (error) => { show("run-status", error.message, true); if (initial) { show("records-status", "Original history is unavailable."); show("checkpoints-status", "Unavailable"); show("report-status", "Publication status unknown."); show("datasets-status", "Publication status unknown."); } });
  }
  function loadCheckpoints(more = false, incremental = false) {
    if (incremental && state.channels.has("checkpoints")) return;
    $("more-checkpoints").disabled = true;
    const targetRevision = state.manifest.through_revision;
    const query = new URLSearchParams({ after_revision: String(incremental ? state.checkpointSyncRevision : more ? state.checkpointAfter : 0), limit: "25" });
    request("checkpoints", endpoint(`/data/checkpoints?${query}`), (page) => {
      if (page.run_id !== state.run) throw new Error("Checkpoint source belongs to another run.");
      if (!more && !incremental) state.checkpoints = [];
      const combined = new Map(state.checkpoints.map(item => [item.revision,item]));
      for (const item of page.checkpoints || []) combined.set(item.revision,item);
      state.checkpoints = [...combined.values()].sort((a,b) => a.revision-b.revision);
      if (!incremental) { state.checkpointAfter = page.next_after_revision ?? state.checkpointAfter; state.checkpointHasMore = page.has_more === true; }
      if (!more) state.checkpointSyncRevision = incremental && page.has_more ? (page.next_after_revision ?? state.checkpointSyncRevision) : targetRevision;
      $("checkpoints").replaceChildren();
      for (const item of state.checkpoints) {
        const box = document.createElement("article"), title = document.createElement("strong"), description = document.createElement("p"); box.className = "checkpoint";
        title.textContent = item.kind === "week" ? `Week ${item.week ?? Math.floor(item.tick / 7)} replay` : item.kind === "save_marker" ? "Database save marker" : item.kind === "terminal" ? "Final replay" : item.kind === "opening" ? "Opening replay" : `${label(item.kind)} replay`;
        description.textContent = `Day ${number(item.tick)} · revision ${number(item.revision)}${item.replay_available ? " · saved world available" : " · no replay world retained"}`;
        box.append(title,description);
        if (item.replay_available && item.frame_url) { const button = document.createElement("button"); button.textContent = "Open saved world"; button.addEventListener("click", () => loadFrame(item)); box.append(button); }
        $("checkpoints").append(box);
      }
      show("checkpoints-status", state.checkpoints.length ? `${number(state.checkpoints.length)} retained checkpoints` : "No retained checkpoints found; availability is not inferred from simulation days.");
      $("more-checkpoints").hidden = !state.checkpointHasMore; $("more-checkpoints").disabled = false;
    }, (error) => { show("checkpoints-status", error.message, true); $("more-checkpoints").disabled = false; });
  }
  function loadFrame(item) {
    const expected = endpoint("/renderer/frames/");
    if (typeof item.frame_url !== "string" || !item.frame_url.startsWith(expected)) { show("frame-status", "Saved frame URL is not a trusted run source.", true); return; }
    $("frame-details").hidden = false; $("frame-details").open = true; show("frame-status", "Loading saved world…"); show("frame-json");
    request("frame", item.frame_url, value => { show("frame-status", `Saved world at day ${number(item.tick)}, revision ${number(item.revision)}.`); $("frame-json").textContent = json(value); }, error => show("frame-status", error.message, true));
  }
  function recordScope() {
    const period = $("record-period").value, scope = { kind: $("record-kind").value.trim(), period };
    if (period === "opening") { scope.from_tick = 0; scope.to_tick = 0; }
    else if (period.startsWith("week:")) { const week = Number(period.slice(5)); scope.from_tick = (week - 1) * 7 + 1; scope.to_tick = week * 7; }
    else if (period === "days") scope.from_tick = 1;
    else if (period === "custom") {
      for (const [id,key] of [["from-tick","from_tick"],["to-tick","to_tick"]]) {
        const input = $(id).value;
        if (input !== "") { const value = Number(input); if (!Number.isSafeInteger(value) || value < 0) throw new Error("Days must be non-negative whole numbers."); scope[key] = value; }
      }
      if (scope.from_tick !== undefined && scope.to_tick !== undefined && scope.from_tick > scope.to_tick) throw new Error("The first day must not be after the last day.");
    }
    return scope;
  }
  function recordMatches(record, scope) {
    const frame = record.payload?.frame_kind ?? record.frame_kind;
    if (scope.period === "end") return frame === "end";
    if (scope.period === "days" || scope.period.startsWith("week:")) return frame !== "end";
    return true;
  }
  function loadRecords(reset = false) {
    if (!state.manifest) return;
    if (state.manifest.analytics_enabled === false || state.manifest.audit_status === "not_captured") {
      state.records = []; $("records").replaceChildren(); show("records-status", "Daily analytics records were not captured for this run. Retained replay frames may still be available."); $("more-records").hidden = true; renderCoverage(); return;
    }
    if (["unavailable", "unknown"].includes(state.manifest.audit_status)) { show("records-status", "Original committed history is unavailable or its capture is unknown."); return; }
    if (reset) {
      try { state.recordScope = recordScope(); } catch (error) { show("records-status", error.message, true); return; }
      state.recordAfter = 0; state.records = []; $("records").replaceChildren(); show("page-fields");
      state.recordPin = { through_revision: state.manifest.through_revision, through_sequence: state.manifest.through_sequence };
    }
    const scope = state.recordScope, query = new URLSearchParams({ ...state.recordPin, after: String(state.recordAfter), limit: "100" });
    for (const key of ["from_tick","to_tick","kind"]) if (scope[key] !== undefined && scope[key] !== "") query.set(key, String(scope[key]));
    $("more-records").disabled = true; show("records-status", "Reading the next bounded page…");
    request("records", endpoint(`/data/records?${query}`), (page) => {
      if (page.run_id !== state.run || page.through_revision !== state.recordPin.through_revision || page.through_sequence !== state.recordPin.through_sequence) throw new Error("Record page does not match the selected saved source.");
      const previousAfter = state.recordAfter;
      state.records = (page.records || []).filter(record => recordMatches(record,scope));
      state.recordAfter = page.next_after ?? previousAfter; state.recordMore = page.has_more === true;
      if (state.recordMore && state.recordAfter <= previousAfter) throw new Error("The saved-history cursor did not advance. Refresh this run.");
      $("records").replaceChildren();
      for (const record of state.records) $("records").append(details(record, `#${record.sequence ?? record.seq ?? "?"} · ${record.kind ?? "record"} · day ${record.tick ?? "?"}${record.payload?.frame_kind ? ` · ${record.payload.frame_kind}` : ""}`));
      const count = state.records.length;
      show("records-status", count ? `${count} records in this page · revision ${state.recordPin.through_revision}.${state.recordMore ? " More records remain; continue." : " End of this saved history."}`
        : state.recordMore ? "No matching records in this scanned page. More records remain; continue."
        : "No matching records in this page. End of this saved history; this is not proof a feature was disabled.");
      $("more-records").hidden = !state.recordMore; $("more-records").disabled = false;
      $("page-fields").textContent = json(fieldInventory(state.records.map(record => record.payload ?? record), 12, 2000)); renderCoverage();
    }, (error) => { state.records = []; $("records").replaceChildren(); show("page-fields"); show("records-status", error.message, true); $("more-records").hidden = true; $("more-records").disabled = false; renderCoverage(); });
  }
  function loadPublications() {
    for (const channel of ["report","datasets","rows"]) cancel(channel);
    state.report = null; state.datasets = null; state.dataset = null; state.datasetRowsShown = 0; state.datasetCursor = null;
    $("report-json").replaceChildren(); $("dataset-rows").replaceChildren(); $("dataset-columns").replaceChildren(); $("dataset-fields").replaceChildren(); show("dataset-catalog-json");
    $("more-dataset-rows").hidden = true; $("report-download").hidden = true;
    for (const id of ["report-section","dataset-select"]) { $(id).replaceChildren(); appendOption($(id), "", "No data available"); $(id).disabled = true; }
    show("report-preview"); show("dataset-status"); show("dataset-rows-status"); show("dataset-coverage-scope"); show("datasets-source", "No published row cache");
    const status = state.manifest.databricks_status;
    show("report-status", label(status)); show("datasets-status", label(status)); renderCoverage();
    if (!["available", "report_only", "stale"].includes(status)) return;
    request("report", endpoint("/analytics/report"), (report) => {
      if (report.run_id !== state.run) throw new Error("Published report belongs to another run.");
      state.report = report; const select = $("report-section"); select.replaceChildren();
      for (const key of Object.keys(report)) appendOption(select,key,label(key)); select.disabled = false;
      if (report.summary) select.value = "summary"; renderReportSection();
      const stale = report.through_revision !== state.manifest.through_revision;
      show("report-status", `${stale ? "Older saved report; not the current run revision" : "Published finished-run report"} · revision ${number(report.through_revision)} · ${report.generated_at || "generation time unknown"}`);
      const preview = report.summary?.agent_preview_limit;
      show("report-preview", Array.isArray(report.agents) ? `Agents section is a preview: ${report.agents.length} shown${preview != null ? `, limit ${preview}` : ""}. Use Databricks row pages for the full published dataset.` : "");
      $("report-download").href = endpoint("/analytics/report"); $("report-download").hidden = false;
    }, (error) => show("report-status", error.message, true));
    request("datasets", endpoint("/analytics/datasets"), (response) => {
      if (response.run_id !== state.run) throw new Error("Dataset publication belongs to another run.");
      const catalog = response.dataset_catalog;
      if (response.status !== "available" || !catalog) { show("datasets-status", label(response.status)); renderCoverage(); return; }
      if (catalog.run_id !== state.run || catalog.qualification_status !== "pass" || !catalog.bundle_manifest_sha256 || catalog.through_revision !== state.manifest.through_revision) throw new Error("No verified current Databricks publication matches this saved run.");
      state.datasets = catalog; $("dataset-catalog-json").textContent = json(catalog);
      show("datasets-status", `Published actual Databricks rows · revision ${number(catalog.through_revision)} · ${catalog.generated_at || "generation time unknown"}`);
      show("datasets-source", "Actual Databricks · cached read-only");
      const select = $("dataset-select"); select.replaceChildren();
      for (const dataset of catalog.datasets || []) appendOption(select, dataset.dataset_id, `${dataset.dataset_id} · ${dataset.availability === "unavailable" ? "table unavailable" : `${number(dataset.row_count)} rows`}`);
      select.disabled = !select.options.length;
      if (!select.options.length) appendOption(select,"","No datasets in this publication");
      else selectDataset(select.value);
      renderCoverage();
    }, (error) => { show("datasets-status", error.message, true); renderCoverage(); });
  }
  function renderReportSection() { $("report-json").textContent = state.report ? json(state.report[$("report-section").value]) : ""; }
  function selectDataset(id) {
    cancel("rows"); state.dataset = state.datasets?.datasets.find(dataset => dataset.dataset_id === id) ?? null;
    state.datasetCursor = null; state.datasetRowsShown = 0;
    for (const key of ["dataset-columns","dataset-fields","dataset-rows"]) $(key).replaceChildren();
    $("more-dataset-rows").hidden = true; show("dataset-rows-status");
    const dataset = state.dataset; if (!dataset) return;
    const unavailable = dataset.availability === "unavailable";
    show("dataset-status", unavailable ? `Table unavailable: ${label(dataset.unavailable_reason)}. No verified row count.` : `${dataset.table_name} · ${number(dataset.row_count)} rows in this run`);
    for (const column of dataset.columns || []) { const row = document.createElement("tr"); cell(row,column.name); cell(row,column.data_type); cell(row,column.nullable === true ? "Yes" : column.nullable === false ? "No" : "Unknown"); cell(row,number(column.non_null_count)); $("dataset-columns").append(row); }
    for (const field of dataset.payload_fields || []) { const row = document.createElement("tr"); cell(row,field.path); for (const key of ["present_count","null_count","non_null_count","zero_or_false_count"]) cell(row,number(field[key])); $("dataset-fields").append(row); }
    show("dataset-coverage-scope", `Coverage: ${label(dataset.field_coverage_scope)}.${dataset.field_coverage_limit ? ` Inventory limit: depth ${dataset.field_coverage_limit.max_depth}; array items ${dataset.field_coverage_limit.array_elements ? "included" : "not individually inventoried"}.` : " Coverage depth limit is unknown."}${dataset.payload_source ? ` JSON source: ${dataset.payload_source}.` : ""}`);
    if (unavailable) return;
    if (dataset.row_count === 0) { show("dataset-rows-status", "Verified table exists with zero rows for this run."); return; }
    loadDatasetRows();
  }
  function loadDatasetRows() {
    const dataset = state.dataset, catalog = state.datasets;
    if (!dataset || !catalog) return;
    const query = new URLSearchParams({ limit: "100" }); if (state.datasetCursor) query.set("cursor",state.datasetCursor);
    show("dataset-rows-status", "Reading full rows from the published cache…"); $("more-dataset-rows").disabled = true;
    request("rows", endpoint(`/analytics/datasets/${encodeURIComponent(dataset.dataset_id)}/rows?${query}`), (page) => {
      if (page.run_id !== state.run || page.dataset_id !== dataset.dataset_id || page.source !== "databricks_cached" || page.source_manifest_sha256 !== catalog.source_manifest_sha256 || page.bundle_manifest_sha256 !== catalog.bundle_manifest_sha256)
        throw new Error("Row page does not match the selected Databricks dataset and publication.");
      $("dataset-rows").replaceChildren();
      for (const [index,row] of (page.rows || []).entries()) $("dataset-rows").append(details(row, `Row ${state.datasetRowsShown + index + 1}${row.tick != null ? ` · day ${row.tick}` : ""}${row.agent_id ? ` · ${row.agent_id}` : ""} · full columns`));
      const start = state.datasetRowsShown + 1; state.datasetRowsShown += (page.rows || []).length;
      state.datasetCursor = page.next_cursor ?? null; state.datasetMore = page.has_more === true;
      if (state.datasetMore && (!state.datasetCursor || state.datasetCursor === query.get("cursor"))) throw new Error("The Databricks row cursor did not advance.");
      show("dataset-rows-status", page.rows?.length ? `Rows ${start}–${state.datasetRowsShown} of ${number(dataset.row_count)} · full columns.${state.datasetMore ? " More rows available." : " End of published dataset."}` : "No rows in this cache page; availability must be checked from its publication evidence.");
      $("more-dataset-rows").hidden = !state.datasetMore; $("more-dataset-rows").disabled = false;
    }, (error) => { $("dataset-rows").replaceChildren(); show("dataset-rows-status", error.message, true); $("more-dataset-rows").hidden = true; $("more-dataset-rows").disabled = false; });
  }
  // Arrays stay intact in raw JSON. The presence inventory records array fields, not their items.
  function fieldInventory(objects, maxDepth = 4, maxPaths = 2000) {
    const fields = new Map(); let truncated = false;
    function walk(value,path,depth) {
      if (path) {
        if (!fields.has(path) && fields.size >= maxPaths) { truncated = true; return; }
        const field = fields.get(path) || { path, present_count:0, null_count:0, non_null_count:0, zero_or_false_count:0 };
        field.present_count++; if (value === null) field.null_count++; else field.non_null_count++;
        if (value === 0 || value === false) field.zero_or_false_count++; fields.set(path,field);
      }
      if (value && typeof value === "object" && !Array.isArray(value)) {
        if (depth >= maxDepth) { if (Object.keys(value).length) truncated = true; return; }
        for (const [key,item] of Object.entries(value)) walk(item, `${path}/${key.replaceAll("~","~0").replaceAll("/","~1")}`,depth+1);
      }
    }
    for (const object of objects) walk(object,"",0);
    return { scope:"displayed_page", record_count:objects.length, max_depth:maxDepth, array_elements:false, truncated, fields:[...fields.values()].sort((a,b) => a.path.localeCompare(b.path)) };
  }
  const features = [
    { name:"Banking / deposits", keys:["banking","deposit_creation"], terms:["bank","banking","deposit_minor","bank_ledger","deposit"] },
    { name:"School, health and public services", keys:["public_services","school"], terms:["school","health","hungry","hunger","public_service","clinic","qol","quality_of_life","is_student","enrolled_students"] },
    { name:"Labor and income distribution", keys:["labor_distribution"], terms:["disposable_income","labor_force","inequality","wage","salary"] },
    { name:"Resources", keys:["resources"], terms:["inventory","stock","endowment","resources"] },
    { name:"Infrastructure", keys:["infrastructure"], terms:["infrastructure","construction","maintenance"] },
    { name:"War", keys:["war","warfare"], terms:["war","warfare","military"] },
    { name:"Central banks", keys:["central_banks","central_bank"], terms:["central_bank","central_banks"] }
  ];
  const matches = (text,terms) => terms.some(term => String(text).split(/[/.]/).some(part => part === term || part.startsWith(`${term}_`) || part.endsWith(`_${term}`) || part.includes(`_${term}_`)));
  function declared(feature) {
    const metadata = state.manifest?.capture_metadata;
    if (!metadata) return "Legacy / unknown";
    const versions = metadata.features || {}, active = Array.isArray(metadata.active_features) ? metadata.active_features : [];
    const found = feature.keys.filter(key => Object.hasOwn(versions,key));
    const values = found.map(key => `${key}: ${versions[key] === null ? "null (unknown)" : versions[key] === false ? "disabled" : json(versions[key])}`);
    const activeKeys = feature.keys.filter(key => active.includes(key));
    if (activeKeys.length) values.push(`Declared active: ${activeKeys.join(", ")}`);
    if (feature.keys.includes("banking") && Object.hasOwn(metadata,"banking_paused")) values.push(`Banking paused: ${metadata.banking_paused === null ? "unknown" : metadata.banking_paused}`);
    return values.join("; ") || (feature.name === "Central banks" ? "No explicit capture contract / unknown" : "Not declared / unknown");
  }
  function originalEvidence(feature) {
    if (state.manifest?.analytics_enabled === false || state.manifest?.audit_status === "not_captured") return "Not captured: analytics disabled";
    if (!state.records.length) return "Unknown / no displayed record evidence";
    const fields = fieldInventory(state.records.map(record => record.payload ?? record)).fields.filter(field => matches(field.path,feature.terms));
    const kinds = state.records.filter(record => matches(record.kind || "",feature.terms));
    if (!fields.length && !kinds.length) return "Not present in this bounded page";
    const evidence = fields.slice(0,5).map(field => `${field.path}: ${field.present_count} present, ${field.null_count} null, ${field.zero_or_false_count} zero/false`);
    if (kinds.length) evidence.push(`${kinds.length} matching records`);
    if (fields.length > 5) evidence.push(`${fields.length-5} more fields in page inventory`);
    return evidence.join("; ");
  }
  function deltaEvidence(feature) {
    if (!state.datasets) return `${label(state.manifest?.databricks_status)} · per-run field coverage unknown`;
    const evidence = [];
    for (const dataset of state.datasets.datasets || []) {
      if (dataset.availability === "unavailable") continue;
      const typed = (dataset.columns || []).filter(column => matches(column.name,feature.terms));
      const fields = (dataset.payload_fields || []).filter(field => matches(field.path,feature.terms));
      if (matches(dataset.dataset_id,feature.terms)) evidence.push(`${dataset.dataset_id}: ${number(dataset.row_count)} rows`);
      for (const column of typed.slice(0,3)) evidence.push(`${dataset.dataset_id}.${column.name}: typed column, ${number(column.non_null_count)} non-null`);
      for (const field of fields.slice(0,3)) evidence.push(`${dataset.dataset_id}${field.path}: payload retained, ${number(field.present_count)} present, ${number(field.null_count)} null, ${number(field.zero_or_false_count)} zero/false${typed.length ? "" : "; typed feature column not shown"}`);
    }
    return evidence.length ? evidence.slice(0,7).join("; ") + (evidence.length > 7 ? "; more in dataset field inventory" : "") : "No matching field in published inventory · unknown beyond its coverage limits";
  }
  function renderCoverage() {
    $("feature-coverage").replaceChildren();
    for (const feature of features) { const row = document.createElement("tr"); cell(row,feature.name); cell(row,declared(feature),"unknown"); cell(row,originalEvidence(feature)); cell(row,deltaEvidence(feature)); $("feature-coverage").append(row); }
  }
  $("data-runs").addEventListener("change", () => selectRun($("data-runs").value));
  $("more-runs").addEventListener("click", () => discover(true));
  $("refresh-run").addEventListener("click", () => { if (state.run) selectRun(state.run); discover(); });
  $("more-checkpoints").addEventListener("click", () => loadCheckpoints(true));
  $("record-filters").addEventListener("submit", event => { event.preventDefault(); loadRecords(true); });
  $("more-records").addEventListener("click", () => loadRecords());
  $("report-section").addEventListener("change", renderReportSection);
  $("dataset-select").addEventListener("change", () => selectDataset($("dataset-select").value));
  $("more-dataset-rows").addEventListener("click", loadDatasetRows);
  $("record-period").addEventListener("change", () => { const custom = $("record-period").value === "custom"; $("from-tick").disabled = !custom; $("to-tick").disabled = !custom; });
  $("from-tick").disabled = true; $("to-tick").disabled = true;
  function refresh() { if (state.destroyed || document.visibilityState === "hidden") return; discover(); if (state.run) loadManifest(); }
  document.addEventListener("visibilitychange", () => { if (document.visibilityState !== "hidden") refresh(); });
  // Expose only navigation/refresh for integration checks; no write operations exist.
  window.GEarthDataExplorer = { refresh, selectRun, fieldInventory };
  const selected = new URLSearchParams(location.search).get("run_id");
  if (selected) selectRun(selected);
  discover(); let timer = setInterval(refresh,10000);
  window.addEventListener("pagehide", () => { state.destroyed = true; state.generation++; for (const channel of [...state.channels.keys()]) cancel(channel); clearInterval(timer); });
  window.addEventListener("pageshow", event => { if (event.persisted && state.destroyed) { state.destroyed = false; timer = setInterval(refresh,10000); refresh(); } });
})();
