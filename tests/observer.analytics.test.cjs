const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const { JSDOM } = require('jsdom');
const root = 'backend/GEarth.Api/wwwroot/';
const html = readFileSync(root + 'index.html', 'utf8');
// Isolate rendering from polling; the existing observer tests cover navigation races.
const script = readFileSync(root + 'observer.js', 'utf8').replace(/refresh\(\);\r?\nsetInterval\(refresh, 1500\);/, '');
function view() {
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: 'http://localhost' });
  dom.window.eval(script);
  return dom;
}
function report() {
  return { schema_version: 'gearth.analytics-report.v1', run_id: 'done', through_revision: 9,
    through_sequence: 21, analysis_version: '1.0.0', source_manifest_sha256: 'a'.repeat(64),
    generated_at: '2026-10-10T12:00:00Z', completeness: { run_status: 'stopped', run_complete: false,
      snapshot_complete: true, missing_data: ['tokens'], metadata_unknown: ['hardware'], synthetic: true },
    summary: { record_count: 21, agent_count: 2, transaction_count: 3, transfer_minor: 400,
      decision_result_count: 2, fallback_count: 1 },
    checks: [{ name: '<img src=x onerror=alert(1)>', status: 'pass', details: { label: '<script>bad()</script>' } }],
    agents: [], countries: [{ country_id: '<b>aster</b>', income_minor: 100 }],
    government_comparison: { status: 'descriptive', reason: 'One observed run' }, evidence_refs: [] };
}
test('imported report is read-only, partial run explicit and payload rendered as text', () => {
  const dom = view(), doc = dom.window.document;
  dom.window.showAnalyticsReport('done', { report: report() });
  assert.match(doc.getElementById('analytics-status').textContent, /STOPPED.*partial run.*revision 9/);
  assert.match(doc.getElementById('analytics-summary').textContent, /21 audit records.*3 transfers.*2 agents/);
  assert.match(doc.getElementById('analytics-summary').textContent, /Missing data: tokens.*hardware/);
  assert.match(doc.getElementById('analytics-checks').textContent, /<img src=x/);
  assert.equal(doc.getElementById('analytics-checks').querySelector('img'), null);
  assert.equal(doc.getElementById('analytics-checks').querySelector('script'), null);
  assert.equal(doc.getElementById('analytics-country-list').querySelector('b'), null);
  assert.equal(doc.getElementById('analytics-download').getAttribute('href'), '/api/runs/done/analytics/report');
  dom.window.close();
});
test('unimported report clears previous totals, country outcomes and download', () => {
  const dom = view(), doc = dom.window.document;
  dom.window.showAnalyticsReport('done', { report: report() });
  dom.window.showAnalyticsReport('other', { report: null, message: 'No imported analytics report is available for this run.' });
  assert.match(doc.getElementById('analytics-status').textContent, /No imported/);
  assert.equal(doc.getElementById('analytics-summary').childElementCount, 0);
  assert.equal(doc.getElementById('analytics-country-list').childElementCount, 0);
  assert.equal(doc.getElementById('analytics-download').hidden, true);
  assert.equal(doc.getElementById('analytics-download').getAttribute('href'), null);
  dom.window.close();
});
test('running worlds never request finished analytics; stale and absent reports are explicit', async () => {
  const dom = view();
  let calls = 0;
  dom.window.fetch = async () => { calls++; return { ok: false, status: 404 }; };
  const signal = new dom.window.AbortController().signal;
  const running = await dom.window.loadAnalyticsReport('/api/runs/done', { run: { id: 'done', status: 'running' } }, signal);
  assert.equal(calls, 0); assert.match(running.message, /Simulation in progress/);
  const absent = await dom.window.loadAnalyticsReport('/api/runs/done', { run: { id: 'done', status: 'stopped', revision: 9 } }, signal);
  assert.equal(calls, 1); assert.match(absent.message, /No imported/);
  dom.window.fetch = async () => ({ ok: true, json: async () => ({ ...report(), through_revision: 8 }) });
  const stale = await dom.window.loadAnalyticsReport('/api/runs/done', { run: { id: 'done', status: 'stopped', revision: 9 } }, signal);
  assert.equal(stale.report, null); assert.match(stale.message, /does not match/);
  dom.window.close();
});
