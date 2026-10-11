import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { packRecording, verifyRecording } from '../renderer/recording.mjs';
import { pipelineFixture } from '../renderer/tests/pipeline_fixture.mjs';

async function fixture(t) {
  const directory=await mkdtemp(join(tmpdir(),'gearth-recording-test-'));
  t.after(()=>rm(directory,{recursive:true,force:true}));
  const data=pipelineFixture(), files=[];
  let sequence=0;
  async function write(path,value) {
    const bytes=Buffer.from(JSON.stringify(value)+'\n');
    await writeFile(join(directory,path),bytes);
    files.push({path,bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')});
  }
  for(const frame of data.frames) {
    const events=data.events.filter(e=>e.seq>sequence&&e.seq<=frame.through_event_sequence);
    if(events.length) await write(`events-${frame.revision}.json`,{...data.metadata,events});
    await write(`frame-${frame.revision}.json`,frame); sequence=frame.through_event_sequence;
  }
  const manifest={...data.metadata,export_schema_version:'gearth.renderer-export.v1',files,
    frame_count:4,through_revision:4,through_event_sequence:sequence};
  await writeFile(join(directory,'manifest.json'),JSON.stringify(manifest));
  return {directory,manifest,path:join(directory,'year.gearth')};
}

test('one-file recording preserves verified export bytes and never overwrites a recording',async t=>{
  const {directory,manifest,path}=await fixture(t);
  await packRecording(directory,path);
  assert.deepEqual(await verifyRecording(path),manifest);
  const before=await readFile(path);
  await assert.rejects(packRecording(directory,path),{code:'EEXIST'});
  assert.deepEqual(await readFile(path),before);
});

test('packing rejects corrupt source data and never publishes a partial recording',async t=>{
  const {directory,manifest,path}=await fixture(t);
  await writeFile(join(directory,manifest.files[0].path),'{}');
  await assert.rejects(packRecording(directory,path),/integrity/);
  await assert.rejects(readFile(path),{code:'ENOENT'});
});

test('opening rejects tampering, truncation, oversized headers and duplicate entries',async t=>{
  const {directory,path}=await fixture(t); await packRecording(directory,path);
  const valid=await readFile(path);
  for(const bytes of [valid.subarray(0,6),valid.subarray(0,valid.length-1),Buffer.concat([valid,Buffer.from('extra')])]) {
    await writeFile(path,bytes); await assert.rejects(verifyRecording(path),/Truncated|size/);
  }
  const corrupt=Buffer.from(valid); corrupt[corrupt.length-2]^=1;
  await writeFile(path,corrupt); await assert.rejects(verifyRecording(path),/integrity/);
  const oversized=Buffer.from(valid); oversized.writeUInt32LE(0xffffffff,8);
  await writeFile(path,oversized); await assert.rejects(verifyRecording(path),/header size/);
  const length=valid.readUInt32LE(8), manifest=JSON.parse(valid.subarray(12,12+length));
  manifest.files[1].path=manifest.files[0].path;
  const header=Buffer.from(JSON.stringify(manifest)), prefix=Buffer.from(valid.subarray(0,12));
  prefix.writeUInt32LE(header.length,8);
  await writeFile(path,Buffer.concat([prefix,header,valid.subarray(12+length)]));
  await assert.rejects(verifyRecording(path),/Invalid recording file entry/);
});

test('native renderer reads packed checkpoints and cargo actions directly from one file',
  {skip:!process.env.GEARTH_RENDERER_EXE},async t=>{
  const {directory,path}=await fixture(t); await packRecording(directory,path);
  const check=tick=>{
    const result=spawnSync(process.env.GEARTH_RENDERER_EXE,['--check-stream',path,'--tick',String(tick)],{encoding:'utf8'});
    assert.equal(result.status,0,result.stderr||result.error?.message); return JSON.parse(result.stdout);
  };
  assert.equal(check(15).frames,4); assert.equal(check(15).state_tick,15);
  assert.equal(check(15).events,10);
  assert.equal(check(15).playback_seconds,60);
  assert.deepEqual(check(7).cargo,[{route:2,progress:.5}]);
  assert.equal(check(3).cargo[0].route,1);
  assert.equal(check(5).cargo.length,0);
  assert.equal(check(2).cargo[0].route,0);
  const bytes=await readFile(path); await writeFile(path,bytes.subarray(0,bytes.length-1));
  const bad=spawnSync(process.env.GEARTH_RENDERER_EXE,['--check-stream',path],{encoding:'utf8'});
  assert.equal(bad.status,1); assert.match(bad.stderr,/recording file size|Recording size/);
});
