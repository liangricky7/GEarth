import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { createWriteStream, createReadStream } from 'node:fs';
import { createHash } from 'node:crypto';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { resolve, dirname } from 'node:path';

// Pin both the scene and its Git blob hashes; no external conversion tools needed.
const revision = '2bdb6a410f8ebd475d3737e7c8e038ed2b00b02e';
const root = resolve('artifacts/local/bistro');
const base = `https://raw.githubusercontent.com/zeux/niagara_bistro/${revision}/`;
await mkdir(root, { recursive: true });
async function get(url) {
  const response = await fetch(url, { signal: AbortSignal.timeout(600000) });
  if (!response.ok) throw new Error(`${response.status}: ${url}`);
  return response;
}
const tree = await (await get(`https://api.github.com/repos/zeux/niagara_bistro/git/trees/${revision}?recursive=1`)).json();
if (tree.truncated || !Array.isArray(tree.tree)) throw new Error('Incomplete asset inventory');
const blobs = new Map(tree.tree.filter(e => e.type === 'blob').map(e => [e.path, e]));
async function download(name) {
  if (name.split('/').some(p => !p || p === '.' || p === '..') || /[\\:]/.test(name)) throw new Error('Unsafe asset path');
  const entry = blobs.get(name);
  if (!entry || !Number.isSafeInteger(entry.size) || entry.size < 0) throw new Error(`Missing asset: ${name}`);
  const file = resolve(root, name);
  const hash = () => createHash('sha1').update(`blob ${entry.size}\0`);
  try {
    const existing = hash();
    for await (const chunk of createReadStream(file)) existing.update(chunk);
    if (existing.digest('hex') === entry.sha) return;
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  await mkdir(dirname(file), { recursive: true });
  const temporary = `${file}.part`;
  try {
    const digest = hash(); let size = 0;
    await pipeline(Readable.fromWeb((await get(base + name)).body), new Transform({
      transform(chunk, encoding, done) {
        size += chunk.length;
        if (size > entry.size) return done(new Error(`Oversized asset: ${name}`));
        digest.update(chunk); done(null, chunk);
      },
    }), createWriteStream(temporary));
    if (size !== entry.size || digest.digest('hex') !== entry.sha) throw new Error(`Asset integrity mismatch: ${name}`);
    await rename(temporary, file);
  } finally { await rm(temporary, { force: true }); }
}
await download('bistro.gltf');
const scene = JSON.parse(await readFile(resolve(root, 'bistro.gltf'), 'utf8'));
const names = [...new Set(['LICENSE', 'README.md', ...scene.buffers.map(b => b.uri),
  ...scene.textures.map(t => scene.images[t.extensions.MSFT_texture_dds.source].uri)])];
let next = 0, completed = 0;
console.log(`Bistro ${revision}: ${names.length} files, ${(names.reduce((n, p) => n + blobs.get(p).size, 0) / 1e9).toFixed(2)} GB; existing files are verified and reused.`);
await Promise.all(Array.from({ length: 8 }, async () => {
  while (next < names.length) {
    const name = names[next++];
    for (let attempt = 0; ; attempt++) {
      try { await download(name); break; }
      catch (error) { if (attempt === 2) throw error; console.log(`Retrying ${name}: ${error.message}`); }
    }
    completed++;
    if (completed % 20 === 0 || completed === names.length) console.log(`${completed}/${names.length}`);
  }
}));
await writeFile(resolve(root, 'SOURCE.txt'), `Amazon Lumberyard Bistro, NVIDIA RTXDI Assets, edited by zeux.\nhttps://github.com/zeux/niagara_bistro/tree/${revision}\nSee LICENSE for the retained NVIDIA MIT notice.\n`);
console.log('Ready: renderer/build/Release/renderer.exe --environment artifacts/local/bistro/bistro.gltf');
