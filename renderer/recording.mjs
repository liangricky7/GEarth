import { readFile, open, link, unlink } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { CONTRACT, playExport } from './client.mjs';

const MAGIC = Buffer.from('GEARTH1\n');
const MAX_HEADER = 8 * 1024 * 1024;
const MAX_RECORD = 256 * 1024 * 1024;
const MAX_FILE = 2 * 1024 * 1024 * 1024;

function validate(manifest, headerBytes) {
  if (manifest?.schema_version !== CONTRACT || manifest.export_schema_version !== 'gearth.renderer-export.v1'
    || !Array.isArray(manifest.files) || !manifest.files.length || manifest.files.length > 10000)
    throw new Error('Unknown recording manifest');
  const paths = new Set();
  let size = 12 + headerBytes;
  for (const file of manifest.files) {
    if (!/^(events|frame)-[a-zA-Z0-9_.-]+\.json$/.test(file.path) || paths.has(file.path)
      || !Number.isSafeInteger(file.bytes) || file.bytes < 1 || file.bytes > MAX_RECORD
      || !/^[a-f0-9]{64}$/.test(file.sha256)) throw new Error('Invalid recording file entry');
    paths.add(file.path); size += file.bytes;
  }
  if (headerBytes > MAX_HEADER || size > MAX_FILE) throw new Error('Recording exceeds size limit');
  return size;
}

async function readExact(file, buffer, position) {
  let offset = 0;
  while (offset < buffer.length) {
    const { bytesRead } = await file.read(buffer, offset, buffer.length - offset, position + offset);
    if (!bytesRead) throw new Error('Truncated recording');
    offset += bytesRead;
  }
}

// Keep the original JSON bytes and hashes; bundling never changes the public export contract.
export async function packRecording(folder, output) {
  const manifest = JSON.parse(await readFile(join(folder, 'manifest.json'), 'utf8'));
  const header = Buffer.from(JSON.stringify(manifest));
  validate(manifest, header.length);
  const prefix = Buffer.alloc(12); MAGIC.copy(prefix); prefix.writeUInt32LE(header.length, 8);
  const temporary = `${output}.${randomUUID()}.tmp`;
  const file = await open(temporary, 'wx');
  try {
    await file.writeFile(prefix); await file.writeFile(header);
    await playExport({ manifest, onFrame: async () => {}, loadFile: async name => {
      const bytes = await readFile(join(folder, name));
      await file.writeFile(bytes); return bytes;
    } });
    await file.close();
    await link(temporary, output); // Publish only a complete recording, without replacing an existing file.
  } finally { await file.close(); await unlink(temporary); }
  return manifest;
}

export async function verifyRecording(path) {
  const file = await open(path, 'r');
  try {
    const prefix = Buffer.alloc(12); await readExact(file, prefix, 0);
    if (!prefix.subarray(0, 8).equals(MAGIC)) throw new Error('Unknown recording format');
    const length = prefix.readUInt32LE(8);
    if (!length || length > MAX_HEADER) throw new Error('Invalid recording header size');
    const header = Buffer.alloc(length); await readExact(file, header, 12);
    const manifest = JSON.parse(header.toString('utf8'));
    if ((await file.stat()).size !== validate(manifest, length)) throw new Error('Recording size does not match manifest');
    const buffer = Buffer.alloc(1024 * 1024);
    let position = 12 + length;
    for (const entry of manifest.files) {
      const hash = createHash('sha256');
      let remaining = entry.bytes;
      while (remaining) {
        const chunk = buffer.subarray(0, Math.min(remaining, buffer.length));
        await readExact(file, chunk, position); hash.update(chunk);
        position += chunk.length; remaining -= chunk.length;
      }
      if (hash.digest('hex') !== entry.sha256) throw new Error('Recording file integrity check failed');
    }
    return manifest;
  } finally { await file.close(); }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.length !== 4) throw new Error('Usage: node renderer/recording.mjs EXPORT_FOLDER OUTPUT.gearth');
    const manifest = await packRecording(resolve(process.argv[2]), resolve(process.argv[3]));
    console.log(`Packed ${manifest.frame_count} checkpoints through day ${manifest.checkpoint_tick}: ${process.argv[3]}`);
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
