const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { test } = require('node:test');
const { JSDOM } = require('jsdom');
const root = 'backend/GEarth.Api/wwwroot/';
const html = readFileSync(root + 'data.html', 'utf8');
const script = readFileSync(root + 'data.js', 'utf8');
const digest = 'a'.repeat(64), bundle = 'b'.repeat(64);
const response = (body, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => structuredClone(body) });
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
async function until(predicate) { for (let i = 0; i < 200; i++) { if (predicate()) return; await new Promise(r => setTimeout(r, 5)); } throw new Error('Data explorer did not reach the expected state'); }
function fixture(id = 'A', options = {}) {
  const catalog = { schema_version:'gearth.dataset-catalog.v1', run_id:id, through_revision:12, through_sequence:4200,
    qualification_status:'pass', source_manifest_sha256:digest, bundle_manifest_sha256:bundle,
    datasets:[{ dataset_id:'silver.agent_daily', table_name:'workspace.gearth_silver.agent_daily', availability:'available', row_count:205,
      columns:[{ name:'deposit_minor', data_type:'bigint', nullable:true, non_null_count:0 }, { name:'payload', data_type:'string', nullable:true, non_null_count:205 }],
      payload_fields:[{ path:'/deposit_minor', present_count:205, null_count:1, non_null_count:204, zero_or_false_count:203 }], field_coverage_scope:'all_run_rows', field_coverage_limit:{ max_depth:4,array_elements:false } },
      { dataset_id:'silver.bank_ledger', availability:'unavailable', unavailable_reason:'table_not_present', row_count:null,columns:[],payload_fields:[],field_coverage_scope:'unavailable' }] };
  return {
    manifest:{ schema_version:'gearth.run-data.v1',run_id:id,storage:'neon_https',through_revision:12,through_sequence:4200,tick:13,status:'completed',
      analytics_enabled:true,audit_daily_through_tick:13,audit_opening_balances_captured:true,audit_status:'available',databricks_status:'available',timeline_status:'unknown',
      capture_metadata:{ features:{ banking:'v1', resources:null },active_features:['deposit_creation'],banking_paused:false },dataset_catalog:catalog,...options },
    checkpoints:{ run_id:id,checkpoints:[{revision:10,tick:6,kind:'save_marker',replay_available:false},{revision:11,tick:7,week:1,kind:'week',replay_available:true,frame_url:`/api/runs/${id}/renderer/frames/11`}],next_after_revision:11,has_more:false },
    records:{run_id:id,through_revision:12,through_sequence:4200,records:[{kind:'agent_day',sequence:9,tick:7,payload:{frame_kind:'day',agent_id:`${id}-agent`,deposit_minor:0,new_feature:{enabled:false},explicit_null:null,text:'<img src=x onerror=bad()>'}}],next_after:500,has_more:false,scanned_count:500 },
    report:{ schema_version:'gearth.analytics-report.v1',run_id:id,through_revision:12,summary:{agent_preview_limit:100},agents:[{agent_id:`${id}-agent`}],banking:{ledger_record_count:5},public_services:{coverage:'partial'},future_section:{hello:'new data'} },
    datasets:{run_id:id,status:'available',dataset_catalog:catalog,bundle_manifest_sha256:bundle},
    rows:{run_id:id,dataset_id:'silver.agent_daily',source:'databricks_cached',source_manifest_sha256:digest,bundle_manifest_sha256:bundle,
      rows:Array.from({length:100},(_,i)=>({agent_id:`${id}-${i}`,tick:i,payload:{new_field:{value:0}}})),next_cursor:'chunk-1',has_more:true}
  };
}
function view(fixtures = {A:fixture()}, intercept, query = '?run_id=A') {
  const dom = new JSDOM(html,{ url:`http://localhost/data.html${query}`,runScripts:'outside-only' });
  const w = dom.window, calls = [], intervals = [];
  w.setInterval = callback => { intervals.push(callback); return 0; };
  w.fetch = async (path,options = {}) => {
    calls.push({path,...options});
    const override = intercept?.(path,options,calls); if (override) return override;
    const url = new URL(path,'http://localhost');
    if (url.pathname === '/api/data/runs') return response({runs:Object.keys(fixtures).map(run_id=>({run_id,status:'completed'})),next_cursor:null,has_more:false});
    const parts = url.pathname.split('/'), id = parts[3], run = fixtures[id];
    if (!run) return response({},404);
    if (parts[4] === 'data') return response(run[parts[5]] || {});
    if (parts[4] === 'renderer') return response({run_id:id,tick:7,agents:[{id:'world-agent'}]});
    if (parts[5] === 'report') return response(run.report);
    if (parts[5] === 'datasets' && parts.length === 6) return response(run.datasets);
    if (parts[5] === 'datasets' && parts[7] === 'rows') return response(run.rows);
    return response({},404);
  };
  w.eval(script); return {dom,w,doc:w.document,calls,intervals};
}
const change = (v,id,value) => { v.doc.getElementById(id).value=value;v.doc.getElementById(id).dispatchEvent(new v.w.Event('change')); };
const click = (v,id) => v.doc.getElementById(id).click();
const close = v => { v.w.dispatchEvent(new v.w.Event("pagehide")); v.dom.window.close(); };
const ready = v => until(()=>v.doc.getElementById('dataset-rows').childElementCount===100);

test('run explorer shows real save markers, full original payloads, report sections and actual schema without writes',async()=>{
  const v=view();await ready(v);
  const checkpoints=v.doc.getElementById('checkpoints');
  assert.match(checkpoints.textContent,/Database save marker.*no replay world retained.*Week 1 replay.*saved world available/s);
  assert.equal(checkpoints.querySelectorAll('button').length,1);checkpoints.querySelector('button').click();
  await until(()=>v.doc.getElementById('frame-json').textContent.includes('world-agent'));
  assert.match(v.doc.getElementById('records').textContent,/new_feature.*enabled.*false.*explicit_null.*null/s);
  assert.equal(v.doc.querySelector('#records img'),null);
  assert.match(v.doc.getElementById('page-fields').textContent,/new_feature\/enabled/);
  assert.match(v.doc.getElementById('records-source').textContent,/Neon/);
  const sections=[...v.doc.getElementById('report-section').options].map(o=>o.value);
  assert.ok(sections.includes('banking'));assert.ok(sections.includes('public_services'));assert.ok(sections.includes('future_section'));
  change(v,'report-section','future_section');assert.match(v.doc.getElementById('report-json').textContent,/new data/);
  assert.match(v.doc.getElementById('report-preview').textContent,/preview.*limit 100/);
  assert.match(v.doc.getElementById('dataset-columns').textContent,/deposit_minorbigintYes0/);
  assert.match(v.doc.getElementById('dataset-fields').textContent,/2051204203/);
  assert.match(v.doc.getElementById('feature-coverage').textContent,/Central banksNo explicit capture contract \/ unknown/);
  assert.ok(v.calls.every(call=>call.method==='GET')); assert.ok(v.calls.every(call=>!call.path.includes('/jobs')&&!call.path.includes('/sql')));
  close(v);
});

test('catalog discovers new IDs while preserving selected older run and pages through old IDs',async()=>{
  let page=0;
  const v=view({A:fixture('A')},path=>{
    if(path.startsWith('/api/data/runs')){const url=new URL(path,'http://localhost');return response(url.searchParams.has('cursor')?{runs:[{run_id:'old',status:'stopped'}],next_cursor:null,has_more:false}:{runs:[{run_id:page++?'new':'A',status:'completed'}],next_cursor:'older-page',has_more:true});}
  });await ready(v);click(v,'more-runs');await until(()=>[...v.doc.getElementById('data-runs').options].some(o=>o.value==='old'));
  v.w.GEarthDataExplorer.refresh();await until(()=>[...v.doc.getElementById('data-runs').options].some(o=>o.value==='new'));
  assert.equal(v.doc.getElementById('data-runs').value,'A');assert.equal(v.doc.getElementById('more-runs').hidden,true);
  assert.ok(v.calls.some(call=>call.path.includes('cursor=older-page')));
  assert.ok(v.calls.filter(call=>call.path.includes('/data/manifest')).every(call=>call.path.includes('/A/')));
  close(v);
});

test('empty scanned page advances and keeps pin while committed head advances',async()=>{
  const f=fixture(), queries=[];
  const v=view({A:f},path=>{
    if(path.includes('/data/records?')){const q=new URL(path,'http://localhost').searchParams;queries.push(q);return response(q.get('after')==='0'?{...f.records,records:[],next_after:500,has_more:true}:f.records);}
  });await ready(v);await until(()=>v.doc.getElementById('records-status').textContent.includes('More records remain'));
  assert.equal(v.doc.getElementById('more-records').hidden,false);
  f.manifest={...f.manifest,through_revision:13,through_sequence:5000};v.w.GEarthDataExplorer.refresh();
  await until(()=>v.doc.getElementById('records-source').textContent.includes('newer saved records'));
  click(v,'more-records');await until(()=>v.doc.getElementById('records').childElementCount===1);
  assert.equal(queries.at(-1).get('after'),'500');assert.equal(queries.at(-1).get('through_revision'),'12');assert.equal(queries.at(-1).get('through_sequence'),'4200');
  close(v);
});

test('week filters use days 1..7 and separate final rows from daily records',async()=>{
  const f=fixture();f.records.records.push({kind:'agent_day',sequence:10,tick:7,payload:{frame_kind:'end',agent_id:'terminal-only'}});
  const v=view({A:f});await ready(v);
  change(v,'record-period','week:1');v.doc.getElementById('record-filters').dispatchEvent(new v.w.Event('submit',{cancelable:true}));
  await until(()=>v.doc.getElementById('records').childElementCount===1);
  assert.ok(v.calls.at(-1).path.includes('from_tick=1'));assert.ok(v.calls.at(-1).path.includes('to_tick=7'));
  assert.ok(!v.doc.getElementById('records').textContent.includes('terminal-only'));
  change(v,'record-period','end');v.doc.getElementById('record-filters').dispatchEvent(new v.w.Event('submit',{cancelable:true}));
  await until(()=>v.doc.getElementById('records').textContent.includes('terminal-only'));
  assert.equal(v.doc.getElementById('records').childElementCount,1);
  close(v);
});

test('dataset pagination reaches rows after 100 and uses opaque cache cursor',async()=>{
  const f=fixture();let pages=0;
  const v=view({A:f},path=>path.includes('/analytics/datasets/silver.agent_daily/rows?')?response(pages++?{...f.rows,rows:[{agent_id:'beyond-100',all_columns:{future:false}}],next_cursor:null,has_more:false}:f.rows):null);
  await ready(v);click(v,'more-dataset-rows');await until(()=>v.doc.getElementById('dataset-rows').textContent.includes('beyond-100'));
  assert.match(v.doc.getElementById('dataset-rows-status').textContent,/Rows 101–101 of 205/);
  assert.match(v.doc.getElementById('dataset-rows').textContent,/all_columns.*future.*false/s);
  assert.ok(v.calls.some(call=>call.path.includes('cursor=chunk-1')));assert.equal(v.doc.getElementById('more-dataset-rows').hidden,true);
  close(v);
});

test('missing Databricks table differs from verified empty table and unknown field values',async()=>{
  const f=fixture();f.datasets.dataset_catalog.datasets.push({dataset_id:'silver.empty',table_name:'actual.empty',availability:'available',row_count:0,columns:[{name:'new_column',data_type:'string',nullable:true,non_null_count:null}],payload_fields:[],field_coverage_scope:'all_run_rows'});
  const v=view({A:f});await ready(v);const rowCalls=()=>v.calls.filter(call=>call.path.includes('/rows?')).length;
  const before=rowCalls();change(v,'dataset-select','silver.bank_ledger');assert.match(v.doc.getElementById('dataset-status').textContent,/Table unavailable.*No verified row count/);assert.equal(rowCalls(),before);
  change(v,'dataset-select','silver.empty');assert.match(v.doc.getElementById('dataset-rows-status').textContent,/Verified table exists with zero rows/);assert.match(v.doc.getElementById('dataset-columns').textContent,/Unknown/);assert.equal(rowCalls(),before);
  close(v);
});

test('legacy no-capture run does not invent zero totals or query absent datasets',async()=>{
  const v=view({A:fixture('A',{analytics_enabled:false,audit_status:'not_captured',databricks_status:'not_captured',capture_metadata:null,audit_daily_through_tick:null})});
  await until(()=>v.doc.getElementById('records-status').textContent.includes('not captured'));
  assert.match(v.doc.getElementById('metadata-status').textContent,/Legacy \/ unknown/);
  assert.equal(v.doc.getElementById('records').childElementCount,0);assert.equal(v.doc.getElementById('dataset-rows').childElementCount,0);
  assert.ok(!v.calls.some(call=>call.path.includes('/analytics/')||call.path.includes('/data/records')));
  assert.match(v.doc.getElementById('feature-coverage').textContent,/per-run field coverage unknown/);
  close(v);
});

test('late run A records, frames, reports and rows cannot leak into run B',async()=>{
  const late=[];
  const v=view({A:fixture('A'),B:fixture('B')},path=>{
    if(path.includes('/runs/A/')&&!path.includes('/manifest')&&!path.includes('/checkpoints')){const d=deferred();late.push({path,d});return d.promise;}
  });await until(()=>late.length>=3);
  change(v,'data-runs','B');await ready(v);
  for(const {path,d} of late){const f=fixture('A');d.resolve(response(path.includes('/data/records')?f.records:path.includes('/report')?f.report:path.includes('/rows?')?f.rows:f.datasets));}
  await new Promise(r=>setTimeout(r,15));
  assert.match(v.doc.getElementById('records').textContent,/B-agent/);assert.ok(!v.doc.getElementById('records').textContent.includes('A-agent'));
  assert.ok(!v.doc.getElementById('dataset-rows').textContent.includes('A-0'));
  assert.equal(v.doc.getElementById('observer-link').getAttribute('href'),'/?run_id=B');
  assert.ok(v.calls.filter(call=>call.path.includes('/runs/A/')&&!call.path.includes('/manifest')&&!call.path.includes('/checkpoints')).every(call=>call.signal.aborted));
  close(v);
});

test('dataset switches abort old source requests and failed cache clears rows',async()=>{
  const f=fixture();f.datasets.dataset_catalog.datasets.push({...f.datasets.dataset_catalog.datasets[0],dataset_id:'silver.other'});
  const d=deferred();const v=view({A:f},path=>path.includes('/silver.agent_daily/rows?')?d.promise:path.includes('/silver.other/rows?')?response({},500):null);
  await until(()=>v.calls.some(call=>call.path.includes('/silver.agent_daily/rows?')));change(v,'dataset-select','silver.other');
  await until(()=>v.doc.getElementById('dataset-rows-status').textContent.includes('HTTP 500'));
  d.resolve(response(f.rows));await new Promise(r=>setTimeout(r,15));assert.equal(v.doc.getElementById('dataset-rows').childElementCount,0);
  assert.ok(v.calls.find(call=>call.path.includes('/silver.agent_daily/rows?')).signal.aborted);
  close(v);
});

test('refresh requests only compact discovery and manifest; hidden page does not poll',async()=>{
  const v=view();await ready(v);const before=v.calls.length;v.w.GEarthDataExplorer.refresh();
  await until(()=>v.calls.length>=before+2);assert.ok(v.calls.slice(before).every(call=>call.path==='/api/runs/A/data/manifest'||call.path.startsWith('/api/data/runs?')));
  Object.defineProperty(v.doc,'visibilityState',{configurable:true,value:'hidden'});const hiddenBefore=v.calls.length;v.w.GEarthDataExplorer.refresh();assert.equal(v.calls.length,hiddenBefore);
  close(v);
});

test('original presence inventory keeps absent, null, false and zero distinct and raw arrays intact',()=>{
  const v=view({A:fixture('A',{analytics_enabled:false,audit_status:'not_captured',databricks_status:'not_captured'})});
  const inventory=v.w.GEarthDataExplorer.fieldInventory([{absentHere:null,zero:0,bool:false,array:[{notFlattened:1}]},{zero:3}]);
  const fields=new Map(inventory.fields.map(field=>[field.path,field]));
  assert.equal(fields.get('/absentHere').present_count,1);assert.equal(fields.get('/absentHere').null_count,1);
  assert.equal(fields.get('/zero').present_count,2);assert.equal(fields.get('/zero').zero_or_false_count,1);assert.equal(fields.get('/bool').zero_or_false_count,1);
  assert.ok(fields.has('/array'));assert.ok(!fields.has('/array/0/notFlattened'));assert.ok(!fields.has('/missing'));
  close(v);
});

test('source identity mismatch is shown as a failure, never as a valid row count',async()=>{
  const f=fixture();f.rows.source_manifest_sha256='c'.repeat(64);
  const v=view({A:f});await until(()=>v.doc.getElementById('dataset-rows-status').classList.contains('error'));
  assert.match(v.doc.getElementById('dataset-rows-status').textContent,/does not match/);assert.equal(v.doc.getElementById('dataset-rows').childElementCount,0);
  close(v);
});


test('new committed weekly checkpoints appear automatically without replacing the selected run or daily pin',async()=>{
  const f=fixture(), seen=[];
  const v=view({A:f},path=>{
    if(path.includes('/data/checkpoints?')){seen.push(path);const after=Number(new URL(path,'http://localhost').searchParams.get('after_revision'));return response(after>=12?{run_id:'A',checkpoints:[{revision:13,tick:14,week:2,kind:'week',replay_available:true,frame_url:'/api/runs/A/renderer/frames/13'}],next_after_revision:13,has_more:false}:f.checkpoints);}
  });await ready(v);await until(()=>v.doc.getElementById('checkpoints').textContent.includes('Week 1 replay'));
  f.manifest={...f.manifest,through_revision:13,through_sequence:5000,tick:14};v.w.GEarthDataExplorer.refresh();
  await until(()=>v.doc.getElementById('checkpoints').textContent.includes('Week 2 replay'));
  assert.match(v.doc.getElementById('checkpoints').textContent,/Database save marker.*Week 1 replay.*Week 2 replay/s);
  assert.ok(seen.some(path=>path.includes('after_revision=12')));
  assert.match(v.doc.getElementById('records-source').textContent,/viewing revision 12/);close(v);
});


test('a newly published dataset cancels in-flight pages from an older publication of the same run',async()=>{
  const f=fixture(), oldPage=structuredClone(f.rows), d=deferred();let rowCalls=0;
  const v=view({A:f},path=>path.includes('/silver.agent_daily/rows?') ? (rowCalls++ === 0 ? d.promise : response(f.rows)) : null);
  await until(()=>rowCalls===1);
  f.datasets.dataset_catalog.bundle_manifest_sha256='c'.repeat(64);f.datasets.bundle_manifest_sha256='c'.repeat(64);
  f.rows={...f.rows,bundle_manifest_sha256:'c'.repeat(64),rows:[{agent_id:'new-publication',value:0}],next_cursor:null,has_more:false};
  v.w.GEarthDataExplorer.refresh();
  await until(()=>v.doc.getElementById('dataset-rows').textContent.includes('new-publication'));
  d.resolve(response(oldPage));await new Promise(r=>setTimeout(r,15));
  assert.ok(!v.doc.getElementById('dataset-rows').textContent.includes('A-0'));
  assert.ok(v.calls.find(call=>call.path.includes('/silver.agent_daily/rows?')).signal.aborted);close(v);
});

test('opening and final observations are distinct and non-captured imports remain unknown rather than empty datasets',async()=>{
  const f=fixture('A',{databricks_status:'report_only'});f.datasets={run_id:'A',status:'report_only',dataset_catalog:null};
  const v=view({A:f});await until(()=>v.doc.getElementById('report-section').disabled===false);
  await until(()=>v.doc.getElementById('datasets-status').textContent.includes('row cache unavailable'));
  assert.equal(v.doc.getElementById('dataset-select').disabled,true);
  change(v,'record-period','opening');v.doc.getElementById('record-filters').dispatchEvent(new v.w.Event('submit',{cancelable:true}));
  await until(()=>v.calls.some(call=>call.path.includes('from_tick=0')&&call.path.includes('to_tick=0')));
  assert.ok([...v.doc.getElementById('record-period').options].some(option=>option.value==='end'));
  assert.ok(!v.calls.some(call=>call.path.includes('/rows?')));close(v);
});
