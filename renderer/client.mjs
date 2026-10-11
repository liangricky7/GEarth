export const CONTRACT = "gearth.renderer.v1";

/** Feed an exported folder into the same callbacks, verifying its immutable file manifest. */
export async function playExport({ manifest, loadFile, onFrame, onEvents = async () => {}, cryptoImpl = globalThis.crypto }) {
  if (manifest?.schema_version !== CONTRACT || manifest.export_schema_version !== "gearth.renderer-export.v1")
    throw new Error("Unknown renderer export contract");
  if (typeof onFrame !== "function" || typeof loadFile !== "function") throw new Error("Export callbacks are required");
  let sequence = 0, revision = 0, frames = 0;
  for (const file of manifest.files) {
    if (!/^[a-zA-Z0-9_.-]+\.json$/.test(file.path)) throw new Error("Unsafe export path");
    const bytes = new Uint8Array(await loadFile(file.path));
    const hash = [...new Uint8Array(await cryptoImpl.subtle.digest("SHA-256", bytes))]
      .map(byte => byte.toString(16).padStart(2, "0")).join("");
    if (bytes.byteLength !== file.bytes || hash !== file.sha256) throw new Error("Export file integrity check failed");
    const value = checkContract(JSON.parse(new TextDecoder().decode(bytes)), manifest.run_id);
    if (file.path.startsWith("events-")) {
      for (const event of value.events) {
        if (event.run_id !== manifest.run_id || event.seq !== sequence + 1)
          throw new Error("Noncontiguous export events");
        sequence = event.seq;
      }
      await onEvents(value.events);
    } else if (file.path.startsWith("frame-")) {
      if (value.revision <= revision || value.revision > manifest.through_revision
        || value.through_event_sequence !== sequence) throw new Error("Out-of-order export frame");
      await onFrame(value);
      revision = value.revision;
      frames++;
    } else throw new Error("Unknown export file kind");
  }
  if (revision !== manifest.through_revision || sequence !== manifest.through_event_sequence || frames !== manifest.frame_count)
    throw new Error("Export coverage does not match manifest");
  return { frames, afterRevision: revision, afterEventSequence: sequence };
}

function checkContract(value, runId) {
  if (value?.schema_version !== CONTRACT || value.run_id !== runId)
    throw new Error("Renderer contract or run identity mismatch");
  return value;
}

/** Push authoritative weekly states and event pages into Kenny's renderer callbacks. */
export function createReplayClient({ baseUrl, runId, onFrame, onEvents = async () => {},
  fetchImpl = globalThis.fetch, afterRevision = 0, afterEventSequence = 0, signal }) {
  if (typeof onFrame !== "function") throw new Error("onFrame callback is required");
  const root = `${baseUrl.replace(/\/$/, "")}/api/runs/${encodeURIComponent(runId)}/renderer`;
  let revision = afterRevision;
  let sequence = afterEventSequence;
  let busy = false;

  async function read(path) {
    const response = await fetchImpl(root + path, { signal, credentials: "omit" });
    if (!response.ok) throw new Error(`Renderer request failed: ${response.status}`);
    return checkContract(await response.json(), runId);
  }

  async function drainEvents(through) {
    while (sequence < through) {
      const page = await read(`/events?after_seq=${sequence}&through_seq=${through}&limit=500`);
      if (page.through_event_sequence !== through || !Array.isArray(page.events) || !page.events.length)
        throw new Error("Recorded event history is incomplete");
      let expected = sequence + 1;
      for (const event of page.events) {
        if (event.run_id !== runId || event.seq !== expected++ || event.seq > through)
          throw new Error("Noncontiguous renderer event page");
      }
      if (page.next_after_seq !== expected - 1) throw new Error("Invalid event cursor");
      await onEvents(page.events);
      sequence = page.next_after_seq;
    }
  }

  async function sync({ throughRevision = Infinity } = {}) {
    if (busy) throw new Error("A replay sync is already running");
    busy = true;
    let delivered = 0;
    try {
      let more = true;
      while (more) {
        const page = await read(`/frames?after_revision=${revision}&limit=20`);
        for (const head of page.frames) {
          if (head.revision > throughRevision) return delivered;
          if (head.run_id !== runId || head.revision <= revision || head.through_event_sequence < sequence)
            throw new Error("Out-of-order renderer frame");
          const frame = await read(`/frames/${head.revision}`);
          if (frame.revision !== head.revision || frame.tick !== head.tick
            || frame.through_event_sequence !== head.through_event_sequence)
            throw new Error("Renderer frame does not match its index");
          await drainEvents(frame.through_event_sequence);
          await onFrame(frame);
          revision = frame.revision;
          delivered++;
        }
        more = page.has_more;
        if (more && !page.frames.length) throw new Error("Frame cursor did not advance");
      }
      return delivered;
    } finally { busy = false; }
  }

  async function follow({ pollMilliseconds = 5000, onError } = {}) {
    if (!Number.isFinite(pollMilliseconds) || pollMilliseconds < 1000)
      throw new Error("Polling interval must be at least 1000 ms");
    while (!signal?.aborted) {
      try {
        const manifest = await read("/manifest");
        await sync();
        if (["completed", "stopped", "failed"].includes(manifest.status)) return;
      } catch (error) {
        if (signal?.aborted) return;
        if (!onError) throw error;
        await onError(error);
      }
      await new Promise(resolve => {
        const done = () => { clearTimeout(timer); signal?.removeEventListener("abort", done); resolve(); };
        const timer = setTimeout(done, pollMilliseconds);
        signal?.addEventListener("abort", done, { once: true });
        if (signal?.aborted) done();
      });
    }
  }

  return { manifest: () => read("/manifest"), current: () => read("/current"), sync, follow,
    cursor: () => ({ afterRevision: revision, afterEventSequence: sequence }) };
}
