(() => {
  const panel = document.getElementById("experiments-panel");
  if (!panel) return;
  const el = id => document.getElementById(id);
  const active = new Set(["queued", "provisioning", "running_control", "running_changed"]);
  const labels = {queued:"Queued",provisioning:"Creating isolated timelines",running_control:"Running unchanged control",running_changed:"Running changed timeline",completed:"Comparison complete",cancelled:"Cancelled",failed:"Failed"};
  let enabled = false, authenticated = false, busy = false, preview = null, targetData = null;
  let generation = 0, targetGeneration = 0, resultGeneration = 0, offset = 0, selected = "", polling = false;
  let searchTimer, requestKey = null;
  let sourceRun = el("runs").value;
  function node(tag, text, className) {
    const result = document.createElement(tag);
    if (text !== undefined) result.textContent = text;
    if (className) result.className = className;
    return result;
  }
  function options(select, items) {
    select.replaceChildren(...items.map(([value, text]) => { const item = node("option", text); item.value = value; return item; }));
  }
  function access() {
    el("experiment-create").disabled = !enabled || !authenticated || busy || !sourceRun;
    el("experiments-access").textContent = authenticated ? "Experiments run separately from the shared world." : "Unlock team controls to create an experiment.";
  }
  async function api(path, body) {
    const response = await fetch("/api/control/experiments" + path, body === undefined ? {cache:"no-store"} : {method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});
    let data;
    try { data = await response.json(); }
    catch { throw new Error("The experiment service could not complete the request. Check its status and try again."); }
    if (!response.ok) throw new Error(typeof data.detail === "string" ? data.detail : "The experiment request could not be completed.");
    return data;
  }
  function invalidate() {
    generation++; targetGeneration++; preview = null; targetData = null; requestKey = null; busy = false;
    el("experiment-editor").disabled = true; el("experiment-target").replaceChildren();
  }
  function currentTarget() { return targetData?.targets.find(item => item.id === el("experiment-target").value); }
  function newValue() { return targetData?.fields[el("experiment-field").value]?.choices ? el("experiment-choice").value : Number(el("experiment-value").value); }
  function review() {
    const target = currentTarget(), field = el("experiment-field").value;
    if (!target || !preview) return;
    const value = newValue(), original = target.values[field];
    el("experiment-review").textContent = `Start at day ${preview.tick}. Change ${target.id}: ${targetData.fields[field].label} from ${original} to ${value}. Simulate ${el("experiment-days").value} days in both timelines using rule decisions.`;
    el("experiment-submit").disabled = busy || original === value || !Number.isInteger(Number(el("experiment-days").value));
    requestKey = null;
  }
  function fieldChanged() {
    const target = currentTarget(), field = el("experiment-field").value, definition = targetData?.fields[field];
    if (!target || !definition) return;
    const original = target.values[field], choice = Boolean(definition.choices);
    el("experiment-original").textContent = `Original at day ${preview.tick}: ${original}`;
    el("experiment-choice").hidden = !choice; el("experiment-choice").disabled = !choice;
    el("experiment-value").hidden = choice; el("experiment-value").disabled = choice;
    if (choice) {
      options(el("experiment-choice"), definition.choices.map(value => [value, value.replaceAll("_", " ")]));
      el("experiment-choice").value = original;
    } else {
      for (const property of ["min", "max", "step"]) el("experiment-value")[property] = definition[property];
      el("experiment-value").value = original;
    }
    el("experiment-effect").textContent = field === "government" ? "Switching government also resets default tax, welfare, and unemployment policies and may appoint or remove a planner." : field === "income_tax_percent" ? "This is a one-time policy override. Future government decisions can change the tax rate again." : field === "health" ? "This applies an immediate health change. Subsequent health follows the simulation's normal rules." : "This multiplier changes the agent's future production capacity.";
    review();
  }
  async function targets() {
    if (!preview) return;
    const token = ++targetGeneration, editorToken = generation;
    el("experiment-editor").disabled = true;
    try {
      const data = await api(`/previews/${encodeURIComponent(preview.id)}/targets?scope=${el("experiment-scope").value}&q=${encodeURIComponent(el("experiment-search").value)}&offset=${offset}`);
      if (token !== targetGeneration || editorToken !== generation) return;
      targetData = data;
      options(el("experiment-target"), data.targets.map(item => [item.id, item.label]));
      options(el("experiment-field"), Object.entries(data.fields).map(([key, value]) => [key, value.label]));
      el("experiment-target-page").textContent = data.total ? `${offset + 1}–${offset + data.targets.length} of ${data.total}` : "No matching targets";
      el("experiment-target-prev").disabled = offset === 0;
      el("experiment-target-next").disabled = offset + data.targets.length >= data.total;
      el("experiment-submit").disabled = !data.targets.length;
      el("experiment-editor").disabled = false;
      el("experiment-editor-message").textContent = data.targets.length ? "" : "No matching targets in this checkpoint.";
      fieldChanged();
    } catch (error) {
      if (token === targetGeneration && editorToken === generation) el("experiment-editor-message").textContent = error.message;
    }
  }
  async function openEditor() {
    invalidate();
    const token = generation, runId = sourceRun;
    el("experiment-source").textContent = `Saved run: ${runId}`;
    el("experiment-editor-message").textContent = "Loading weekly checkpoints…";
    el("experiment-preview").disabled = true;
    el("experiment-checkpoint").replaceChildren();
    el("experiment-dialog").showModal();
    try {
      const data = await api(`/runs/${encodeURIComponent(runId)}/checkpoints`);
      if (token !== generation) return;
      options(el("experiment-checkpoint"), data.checkpoints.map(item => [item.revision, `Day ${item.tick} · revision ${item.revision}`]));
      el("experiment-preview").disabled = !data.checkpoints.length;
      el("experiment-editor-message").textContent = data.checkpoints.length ? "Select a checkpoint, then prepare it." : "This run has no resumable weekly checkpoints.";
    } catch (error) { if (token === generation) el("experiment-editor-message").textContent = error.message; }
  }
  async function prepare() {
    invalidate(); const token = generation; busy = true; access();
    el("experiment-preview").disabled = true;
    el("experiment-checkpoint").disabled = true;
    el("experiment-editor-message").textContent = "Pinning this checkpoint in Neon…";
    try {
      const data = await api("/previews", {run_id:sourceRun,revision:Number(el("experiment-checkpoint").value)});
      if (token !== generation) return;
      preview = data; offset = 0;
      el("experiment-source").textContent = `Starting at day ${data.tick} · parent ${data.parent_mode} · experiment RULE`;
      await targets();
    } catch (error) { if (token === generation) el("experiment-editor-message").textContent = error.message; }
    finally { if (token === generation) { busy = false; el("experiment-checkpoint").disabled = false; el("experiment-preview").disabled = false; review(); } access(); }
  }
  async function submit(event) {
    event.preventDefault();
    const target = currentTarget();
    if (!target || !preview || busy) return;
    const token = generation;
    busy = true; el("experiment-submit").disabled = true; access();
    requestKey ??= "request_" + crypto.randomUUID().replaceAll("-", "");
    try {
      const job = await api("", {request_key:requestKey,preview_id:preview.id,days:Number(el("experiment-days").value),
        edit:{scope:el("experiment-scope").value,target_id:target.id,field:el("experiment-field").value,value:newValue(),expected_value:target.values[el("experiment-field").value]}});
      if (token !== generation) return;
      selected = job.id; resultGeneration++; el("experiment-dialog").close();
      await poll();
    } catch (error) { if (token === generation) el("experiment-editor-message").textContent = error.message; }
    finally { if (token === generation) { busy = false; el("experiment-submit").disabled = false; } access(); }
  }
  function svgNode(tag, attributes, text) {
    const result = document.createElementNS("http://www.w3.org/2000/svg", tag);
    for (const [key, value] of Object.entries(attributes)) result.setAttribute(key, value);
    if (text !== undefined) result.textContent = text;
    return result;
  }
  function format(value, metric) { return value === null || value === undefined ? "—" : (value * metric.scale).toLocaleString(undefined, {maximumFractionDigits:2}); }
  function chart(key, data) {
    const metric = data.metrics[key], job = data.experiment;
    const figure = node("figure", undefined, "experiment-chart");
    figure.append(node("figcaption", `${metric.label} (${metric.unit})`));
    const svg = svgNode("svg", {viewBox:"0 0 420 190",role:"img","aria-label":`${metric.label}: control and changed from day ${job.fork_tick} to ${job.end_tick}`});
    const values = Object.values(data.timelines).flatMap(rows => rows.map(row => row[key]).filter(value => value !== null && Number.isFinite(value)));
    if (!values.length) { figure.append(node("p", "Waiting for recorded days.")); return figure; }
    let low = Math.min(...values), high = Math.max(...values);
    if (high === low) { low -= Math.max(1, Math.abs(low) * .05); high += Math.max(1, Math.abs(high) * .05); }
    const x = tick => 58 + (tick - job.fork_tick) / (job.end_tick - job.fork_tick) * 340;
    const y = value => 142 - (value - low) / (high - low) * 115;
    svg.append(svgNode("path", {d:"M58 22V142H398",fill:"none",stroke:"#486057"}));
    svg.append(svgNode("line", {x1:58,x2:58,y1:22,y2:142,stroke:"#cfbf86","stroke-dasharray":"3 3"}));
    svg.append(svgNode("text", {x:58,y:175}, `Edit · day ${job.fork_tick}`));
    svg.append(svgNode("text", {x:398,y:161,"text-anchor":"end"}, `Day ${job.end_tick}`));
    svg.append(svgNode("text", {x:52,y:31,"text-anchor":"end"}, format(high, metric)));
    svg.append(svgNode("text", {x:52,y:145,"text-anchor":"end"}, format(low, metric)));
    for (const [timeline, color] of [["control","#94baff"],["changed","#80e0b2"]]) {
      let path = "", previous = null;
      for (const row of data.timelines[timeline]) {
        if (row[key] === null || !Number.isFinite(row[key])) { previous = null; continue; }
        path += `${previous === row.tick - 1 ? "L" : "M"}${x(row.tick)},${y(row[key])} `;
        previous = row.tick;
      }
      svg.append(svgNode("path", {d:path,fill:"none",stroke:color,"stroke-width":2}));
    }
    figure.append(svg); return figure;
  }
  function renderComparison(data) {
    const container = el("experiment-results"); container.replaceChildren();
    const job = data.experiment;
    container.append(node("p", `${job.edit.target_id} · ${job.edit.field}: ${job.edit.expected_value} → ${job.edit.value} · day ${job.fork_tick} to ${job.end_tick}`, "scope"));
    container.append(node("p", data.complete ? `Complete matched comparison. ${data.aggregation}.` : "Partial results; final differences appear after both timelines complete.", "scope"));
    const legend = node("p", undefined, "experiment-legend");
    legend.append(node("span", "● Unchanged control", "experiment-control"), node("span", "● Changed timeline", "experiment-changed"));
    container.append(legend);
    const charts = node("div", undefined, "experiment-charts");
    const keys = job.edit.scope === "agent" ? ["cash_minor","net_wealth_minor","health","wellbeing"] : ["unemployment_rate","approval","treasury_minor","liquid_assets_gini"];
    for (const key of keys) charts.append(chart(key, data));
    container.append(charts);
    const control = data.timelines.control.at(-1), changed = data.timelines.changed.at(-1);
    const table = node("table", undefined, "experiment-table"), header = node("tr");
    for (const text of ["Outcome", `Control${control ? " · day " + control.tick : ""}`, `Changed${changed ? " · day " + changed.tick : ""}`, "Difference"]) header.append(node("th", text));
    const head = node("thead"); head.append(header); table.append(head);
    const body = node("tbody");
    for (const [key, metric] of Object.entries(data.metrics)) {
      const row = node("tr"), left = control?.[key], right = changed?.[key];
      for (const text of [`${metric.label} (${metric.unit})`,format(left, metric),format(right, metric),data.complete && left != null && right != null ? format(right - left, metric) : "—"]) row.append(node("td", text));
      body.append(row);
    }
    table.append(body); const wrap = node("div", undefined, "experiment-table-wrap"); wrap.append(table); container.append(wrap);
    container.append(node("p", `${data.note} Differences reflect this synthetic model and intervention; an agent's outcome can also depend on country and other agents.`, "scope"));
  }
  async function poll() {
    if (!enabled || polling) return;
    polling = true;
    const pollToken = resultGeneration;
    try {
      const listing = await api("");
      if (pollToken !== resultGeneration) return;
      options(el("experiment-list"), [["", "Select an experiment"],...listing.experiments.map(job => [job.id, `${labels[job.state] || job.state} · ${job.edit.target_id} · ${job.edit.field.replaceAll("_", " ")} → ${job.edit.value} · day ${job.fork_tick}`])]);
      el("experiment-list").value = selected;
      if (!selected) return;
      const id = selected, token = resultGeneration, data = await api(`/${encodeURIComponent(id)}/comparison`);
      if (id !== selected || token !== resultGeneration) return;
      const job = data.experiment;
      el("experiment-progress").textContent = `${labels[job.state] || job.state} · day ${job.tick ?? job.fork_tick}${job.error ? ": " + job.error : ""}`;
      el("experiment-cancel").hidden = !authenticated || !active.has(job.state);
      renderComparison(data);
    } catch (error) { if (pollToken === resultGeneration) el("experiment-progress").textContent = error.message; }
    finally { polling = false; }
  }
  document.addEventListener("gearth-controls", event => { authenticated = event.detail.authenticated; access(); if (!authenticated) { el("experiment-cancel").hidden = true; if (el("experiment-dialog").open) el("experiment-dialog").close(); } });
  document.addEventListener("gearth-run-selected", event => { sourceRun = event.detail.runId ?? ""; invalidate(); if (el("experiment-dialog").open) el("experiment-dialog").close(); access(); });
  el("experiment-create").addEventListener("click", openEditor);
  el("experiment-close").addEventListener("click", () => el("experiment-dialog").close());
  el("experiment-dialog").addEventListener("close", invalidate);
  el("experiment-checkpoint").addEventListener("change", invalidate);
  el("experiment-preview").addEventListener("click", prepare);
  el("experiment-scope").addEventListener("change", () => { offset = 0; targets(); });
  el("experiment-search").addEventListener("input", () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => { offset = 0; targets(); }, 250); });
  el("experiment-target-prev").addEventListener("click", () => { offset = Math.max(0, offset - 50); targets(); });
  el("experiment-target-next").addEventListener("click", () => { offset += 50; targets(); });
  el("experiment-target").addEventListener("change", fieldChanged);
  el("experiment-field").addEventListener("change", fieldChanged);
  for (const id of ["experiment-value","experiment-choice","experiment-days"]) el(id).addEventListener("input", review);
  el("experiment-form").addEventListener("submit", submit);
  el("experiment-list").addEventListener("change", () => { selected = el("experiment-list").value; resultGeneration++; el("experiment-results").replaceChildren(); el("experiment-progress").textContent = ""; el("experiment-cancel").hidden = true; poll(); });
  el("experiment-cancel").addEventListener("click", async () => { const id = selected; try { await api(`/${encodeURIComponent(id)}/cancel`, {}); await poll(); } catch(error) { el("experiment-progress").textContent = error.message; } });
  api("/capabilities").then(data => { enabled = data.enabled; panel.hidden = !enabled; access(); if (enabled) { poll(); setInterval(() => { access(); poll(); }, 3000); } }).catch(() => { panel.hidden = true; });
})();
