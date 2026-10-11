import { readFile, mkdtemp, rm, access, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { parseArgs } from 'node:util';
import { setTimeout } from 'node:timers/promises';
import { createReplayClient, playExport } from './client.mjs';
import { createStream } from './stream.mjs';
import { verifyRecording } from './recording.mjs';

const root = dirname(fileURLToPath(import.meta.url));
const { values } = parseArgs({ options: {
  run: { type: 'string' }, api: { type: 'string', default: 'http://127.0.0.1:5080' },
  export: { type: 'string' }, exe: { type: 'string' },
  smoke: { type: 'boolean' }, frames: { type: 'string' }, capture: { type: 'string' }, help: { type: 'boolean' },
} });
if (values.help) {
  console.log('node renderer/run.mjs --export RECORDING.gearth\nnode renderer/run.mjs --export FOLDER\nnode renderer/run.mjs --run RUN_ID [--api URL]\nOptions: --exe PATH --smoke --capture PATH');
} else {
  const abort = new AbortController();
  let directory, child;
  const stop = () => { abort.abort(); child?.kill(); };
  process.once('SIGINT', stop); process.once('SIGTERM', stop);
  try {
    if (Boolean(values.run) === Boolean(values.export)) throw new Error('Choose --run or --export');
    if (values.frames && (!/^[1-9][0-9]*$/.test(values.frames) || Number(values.frames)>2147483647)) throw new Error('--frames requires a positive integer');
    const exe = resolve(values.exe ?? join(root, 'build', 'Release', process.platform === 'win32' ? 'renderer.exe' : 'renderer'));
    await access(exe).catch(() => { throw new Error('Build the native renderer first: cmake --build renderer/build --config Release'); });
    const recording = values.export && (await stat(resolve(values.export))).isFile() ? resolve(values.export) : null;
    if (!recording) directory = await mkdtemp(join(tmpdir(), 'gearth-'));
    let stream, client, metadata, currentOnly = false;
    if (recording) {
      const started = performance.now();
      metadata = await verifyRecording(recording);
      console.log(`Recording verified in ${((performance.now()-started)/1000).toFixed(2)}s; opening ${metadata.checkpoint_tick} recorded days`);
    } else if (values.export) {
      const folder = resolve(values.export);
      metadata = JSON.parse(await readFile(join(folder, 'manifest.json'), 'utf8'));
      stream = await createStream(directory, metadata);
      await playExport({ manifest: metadata, loadFile: path => readFile(join(folder, path)), ...stream });
      await stream.status('Offline export');
    } else {
      client = createReplayClient({ baseUrl: values.api, runId: values.run, signal: abort.signal,
        onFrame: frame => stream.onFrame(frame), onEvents: events => stream.onEvents(events) });
      metadata = await client.manifest();
      currentOnly = metadata.first_available_tick == null;
      stream = await createStream(directory, metadata, { currentOnly, followLatest: true });
      if (currentOnly) await stream.onFrame(await client.current());
      else await client.sync({ throughRevision: metadata.checkpoint_revision });
    }
    if (!recording && !stream.revision()) throw new Error('No recorded frames to display');
    const args = ['--stream', recording ?? directory];
    if (values.smoke) args.push('--smoke');
    if (values.frames) args.push('--frames', values.frames);
    if (values.capture) args.push('--capture', resolve(values.capture));
    child = spawn(exe, args, { stdio: 'inherit', windowsHide: Boolean(values.smoke) });
    const closed = new Promise((resolve, reject) => {
      child.once('error', error => { abort.abort(); reject(error); });
      child.once('exit', (code, signal) => { abort.abort(); code || signal ? reject(new Error(`Renderer exited: ${code ?? signal}`)) : resolve(); });
    });
    const polling = (async () => {
      if (values.export) return;
      while (!abort.signal.aborted) {
        try {
          metadata = await client.manifest();
          if (currentOnly) {
            const frame = await client.current();
            if (frame.revision > stream.revision()) await stream.onFrame(frame);
          } else await client.sync({ throughRevision: metadata.checkpoint_revision });
          await stream.status(currentOnly ? 'Current snapshot; historical weeks unavailable' : 'Connected', metadata);
          if (['completed', 'stopped', 'failed'].includes(metadata.status)) {
            await stream.status(`Run ${metadata.status}`, metadata); return;
          }
        } catch (error) {
          if (abort.signal.aborted) return;
          console.error(`Renderer connection: ${error.message}; retrying`);
          await stream.status(`Disconnected; retrying: ${error.message}`).catch(error => console.error(`Status update: ${error.message}`));
        }
        try { await setTimeout(5000, undefined, { signal: abort.signal }); } catch { return; }
      }
    })();
    await Promise.all([closed, polling]);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
  finally {
    stop();
    if (directory) await rm(directory, { recursive: true, force: true });
  }
}
