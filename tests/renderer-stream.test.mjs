import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createStream } from '../renderer/stream.mjs';
import { createReplayClient, playExport } from '../renderer/client.mjs';
import { pipelineFixture } from '../renderer/tests/pipeline_fixture.mjs';

async function directory(t) {
  const path=await mkdtemp(join(tmpdir(),'gearth-test-'));
  t.after(()=>rm(path,{recursive:true,force:true})); return path;
}
async function feed(stream,{frames,events}) {
  let sequence=0;
  for (const frame of frames) {
    await stream.onEvents(events.filter(e=>e.seq>sequence&&e.seq<=frame.through_event_sequence));
    await stream.onFrame(frame); sequence=frame.through_event_sequence;
  }
}
test('live client writes complete immutable checkpoints, retaining all entities and real ticks',async t=>{
  const data=pipelineFixture(), path=await directory(t), stream=await createStream(path,data.metadata);
  const client=createReplayClient({baseUrl:'http://example.test',runId:data.metadata.run_id,...stream,fetchImpl:async url=>{
    const u=new URL(url); const p=u.pathname.split('/renderer')[1]; let value;
    if(p==='/frames') value={...data.metadata,frames:data.frames.filter(f=>f.revision>Number(u.searchParams.get('after_revision'))),has_more:false};
    else if(p.startsWith('/frames/')) value=data.frames.find(f=>f.revision===Number(p.split('/').at(-1)));
    else { const after=Number(u.searchParams.get('after_seq')),through=Number(u.searchParams.get('through_seq'));
      const events=data.events.filter(e=>e.seq>after&&e.seq<=through);
      value={...data.metadata,events,through_event_sequence:through,next_after_seq:events.at(-1).seq}; }
    return {ok:true,json:async()=>value};
  }});
  assert.equal(await client.sync(),4); assert.equal(await client.sync(),0);
  const index=JSON.parse(await readFile(join(path,'index.json')));
  const stored=await Promise.all(index.frames.map(async file=>JSON.parse(await readFile(join(path,file)))));
  assert.deepEqual(stored,data.frames);
  assert.deepEqual(stored.map(f=>f.tick),[0,7,14,15]);
  assert.equal(stored[3].complete_week,false);
  assert.deepEqual(stored[2].snapshot.agents,[],'complete frames remove absent identities');
  const before=await readFile(join(path,'index.json'),'utf8');
  await assert.rejects(stream.onFrame({...data.frames[3],run_id:'wrong'}));
  await assert.rejects(stream.onFrame({...data.frames[3],revision:'../../escape'}),/Invalid stream frame/);
  assert.equal(await readFile(join(path,'index.json'),'utf8'),before);
  await stream.status('Disconnected; retrying');
  assert.deepEqual(JSON.parse(await readFile(join(path,'index.json'))).frames,index.frames);
});
test('offline export verification uses the same stream and rejects corrupted content',async t=>{
  const data=pipelineFixture(), path=await directory(t), stream=await createStream(path,data.metadata);
  const files=new Map(); let sequence=0;
  for(const frame of data.frames){
    const events=data.events.filter(e=>e.seq>sequence&&e.seq<=frame.through_event_sequence);
    if(events.length) files.set(`events-${frame.revision}.json`,Buffer.from(JSON.stringify({...data.metadata,events})));
    files.set(`frame-${frame.revision}.json`,Buffer.from(JSON.stringify(frame))); sequence=frame.through_event_sequence;
  }
  const manifest={...data.metadata,export_schema_version:'gearth.renderer-export.v1',through_revision:4,
    through_event_sequence:10,frame_count:4,files:[...files].map(([path,bytes])=>({path,bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')}))};
  await playExport({manifest,loadFile:name=>files.get(name),...stream});
  assert.equal(stream.revision(),4);
  await assert.rejects(playExport({manifest,loadFile:()=>Buffer.from('{}'),onFrame:()=>{}}),/integrity/);
});
test('native loader reconciles weekly snapshots with reroutes, failures and backward seeking',
  {skip:!process.env.GEARTH_RENDERER_EXE},async t=>{
  const data=pipelineFixture(),path=await directory(t),stream=await createStream(path,data.metadata);
  await feed(stream,data);
  const check=tick=>{
    const result=spawnSync(process.env.GEARTH_RENDERER_EXE,['--check-stream',path,'--tick',String(tick)],{encoding:'utf8'});
    assert.equal(result.status,0,result.stderr||result.error?.message); return JSON.parse(result.stdout);
  };
  assert.equal(check(6).state_tick,0); assert.equal(check(7).state_tick,7);
  assert.equal(check(14).state_tick,14); assert.equal(check(15).last_tick,15);
  assert.deepEqual(check(7).cargo,[{route:2,progress:.5}]);
  assert.equal(check(3).cargo[0].route,1,'reroute switches path');
  assert.equal(check(5).cargo.length,0,'failed shipment disappears');
  assert.equal(check(10).cargo.length,0,'delivered shipment disappears');
  assert.equal(check(2).cargo[0].route,0,'backward seek restores earlier route');
  const frame=structuredClone(data.frames[1]); frame.snapshot.shipments[0].edge_remaining_ticks=0;
  await writeFile(join(path,'frame-2.json'),JSON.stringify(frame));
  assert.equal(check(7).cargo[0].progress,0,'zero remaining means next edge has not begun');
  frame.run_id='wrong'; await writeFile(join(path,'frame-2.json'),JSON.stringify(frame));
  const bad=spawnSync(process.env.GEARTH_RENDERER_EXE,['--check-stream',path],{encoding:'utf8'});
  assert.equal(bad.status,1); assert.match(bad.stderr,/Mixed/);
});
test('current-only fallback replaces the snapshot without inventing historical frames',async t=>{
  const data=pipelineFixture(),path=await directory(t);
  const stream=await createStream(path,{...data.metadata,captured_from_opening:false},{currentOnly:true});
  await stream.onFrame(data.frames[2]); await stream.onFrame(data.frames[3]);
  const index=JSON.parse(await readFile(join(path,'index.json')));
  assert.deepEqual(index.frames,['frame-4.json']); assert.deepEqual(index.events,[]);
  if(process.env.GEARTH_RENDERER_EXE) {
    const result=spawnSync(process.env.GEARTH_RENDERER_EXE,['--check-stream',path,'--tick','15'],{encoding:'utf8'});
    assert.equal(result.status,0,result.stderr);
    assert.equal(JSON.parse(result.stdout).events,0);
    assert.equal(JSON.parse(result.stdout).state_tick,15);
  }
});
