import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { pipelineFixture } from '../renderer/tests/pipeline_fixture.mjs';

test('launcher retries a dropped API response and native viewer loads new checkpoints without restarting',
  { skip: !process.env.GEARTH_RENDERER_GPU_TESTS, timeout: 120000 }, async t => {
  assert.ok(process.env.GEARTH_RENDERER_EXE, 'Set GEARTH_RENDERER_EXE for GPU tests');
  const data=pipelineFixture(); let ready=false, dropped=false, requests=0;
  const server=createServer((req,res)=>{
    assert.equal(req.method,'GET'); requests++;
    const url=new URL(req.url,'http://localhost'), path=url.pathname.split('/renderer')[1];
    const count=ready?4:1, frames=data.frames.slice(0,count), last=frames.at(-1);
    const metadata={...data.metadata,status:ready?'completed':'running',checkpoint_revision:last.revision,checkpoint_tick:last.tick};
    if(path==='/manifest'&&ready&&!dropped){ dropped=true; res.writeHead(503); res.end(); return; }
    let value;
    if(path==='/manifest') value=metadata;
    else if(path==='/frames') value={...metadata,frames:frames.filter(f=>f.revision>Number(url.searchParams.get('after_revision'))),has_more:false};
    else if(path.startsWith('/frames/')) value=frames.find(f=>f.revision===Number(path.split('/').at(-1)));
    else if(path==='/events') {
      const after=Number(url.searchParams.get('after_seq')),through=Number(url.searchParams.get('through_seq'));
      const events=data.events.filter(e=>e.seq>after&&e.seq<=through);
      value={...metadata,events,through_event_sequence:through,next_after_seq:events.at(-1)?.seq??after};
    } else { res.writeHead(404);res.end();return; }
    res.setHeader('Content-Type','application/json'); res.end(JSON.stringify(value));
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  t.after(()=>new Promise(resolve=>server.close(resolve)));
  const child=spawn(process.execPath,['renderer/run.mjs','--run','integration','--api',`http://127.0.0.1:${server.address().port}`,
    '--exe',process.env.GEARTH_RENDERER_EXE,'--smoke','--frames','3000'],{windowsHide:true});
  t.after(()=>child.kill());
  let output='',errors='';
  child.stdout.on('data',data=>{ output+=data; if(output.includes('GEarth stream: 1 frames')) ready=true; });
  child.stderr.on('data',data=>{ errors+=data; });
  const code=await new Promise((resolve,reject)=>{ child.once('error',reject);child.once('exit',resolve); });
  assert.equal(code,0,errors+output);
  assert.ok(dropped,'connection failure was exercised');
  assert.match(errors,/503.*retrying/);
  assert.match(output,/GEarth stream: 1 frames through day 0/);
  assert.match(output,/GEarth stream: 4 frames through day 15/);
  assert.equal((output.match(/GPU:/g)||[]).length,1,'viewer must not restart');
  assert.ok(requests>10);
});
