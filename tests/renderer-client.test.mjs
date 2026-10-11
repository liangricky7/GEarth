import test from "node:test";
import assert from "node:assert/strict";
import { CONTRACT, createReplayClient, playExport } from "../renderer/client.mjs";
import { createHash } from "node:crypto";

const envelope = value => ({ schema_version: CONTRACT, run_id: "test", ...value });
const event = seq => ({ run_id: "test", seq, event_type: "goods_purchased", tick: 1 });
const frame = (revision, tick, through) => envelope({ revision, tick,
  through_event_sequence: through, snapshot: { agents: [{ id: "agent" }] } });
const heads = [frame(1, 0, 1), frame(3, 7, 3)];

function transport({ gap = false, wrongRun = false, terminal = false } = {}) {
  return async url => {
    const u = new URL(url);
    const path = u.pathname.split("/renderer")[1];
    let data;
    if (path === "/manifest") data = envelope({ status: terminal ? "completed" : "running" });
    else if (path === "/frames") {
      const after = Number(u.searchParams.get("after_revision"));
      data = envelope({ frames: heads.filter(f => f.revision > after), has_more: false });
    } else if (path.startsWith("/frames/")) data = heads.find(f => f.revision === Number(path.split("/").pop()));
    else if (path === "/events") {
      const after = Number(u.searchParams.get("after_seq")), through = Number(u.searchParams.get("through_seq"));
      const next = gap ? after + 2 : after + 1;
      data = envelope({ events: [event(next)], next_after_seq: next, through_event_sequence: through });
    } else throw new Error(`Unexpected path ${path}`);
    return { ok: true, json: async () => wrongRun ? { ...data, run_id: "other" } : data };
  };
}

test("pushes ordered event pages and weekly frames, then resumes without duplicates", async () => {
  const delivered = [];
  const client = createReplayClient({ baseUrl: "https://sim.example", runId: "test", fetchImpl: transport(),
    onEvents: es => delivered.push(...es.map(e => `event:${e.seq}`)),
    onFrame: f => delivered.push(`frame:${f.tick}`) });
  assert.equal(await client.sync(), 2);
  assert.deepEqual(delivered, ["event:1", "frame:0", "event:2", "event:3", "frame:7"]);
  assert.deepEqual(client.cursor(), { afterRevision: 3, afterEventSequence: 3 });
  assert.equal(await client.sync(), 0);
  assert.equal(delivered.length, 5);
});

test("failed renderer callback leaves the frame retriable without redelivering committed events", async () => {
  let fail = true, events = 0;
  const client = createReplayClient({ baseUrl: "https://sim.example", runId: "test", fetchImpl: transport(),
    onEvents: es => { events += es.length; },
    onFrame: () => { if (fail) throw new Error("Renderer not ready"); } });
  await assert.rejects(client.sync(), /Renderer not ready/);
  assert.deepEqual(client.cursor(), { afterRevision: 0, afterEventSequence: 1 });
  fail = false;
  assert.equal(await client.sync(), 2);
  assert.equal(events, 3);
});

test("rejects missing events and mixed run identities", async () => {
  for (const options of [{ gap: true }, { wrongRun: true }]) {
    const client = createReplayClient({ baseUrl: "https://sim.example", runId: "test", fetchImpl: transport(options), onFrame: () => {} });
    await assert.rejects(client.sync(), /Noncontiguous|identity mismatch/);
    assert.equal(client.cursor().afterRevision, 0);
  }
});

test("pinned sync stops before a later live revision, and terminal follow drains all frames", async () => {
  const delivered = [];
  const client = createReplayClient({ baseUrl: "https://sim.example", runId: "test", fetchImpl: transport({ terminal: true }),
    onFrame: f => delivered.push(f.tick) });
  assert.equal(await client.sync({ throughRevision: 1 }), 1);
  await client.follow();
  assert.deepEqual(delivered, [0, 7]);
});

test("offline exports verify hashes before feeding frames and reject corruption", async () => {
  const content = new TextEncoder().encode(JSON.stringify(frame(1, 0, 0)));
  const manifest = envelope({ export_schema_version: "gearth.renderer-export.v1", through_revision: 1,
    through_event_sequence: 0, frame_count: 1, files: [{ path: "frame-000000000001.json",
      bytes: content.byteLength, sha256: createHash("sha256").update(content).digest("hex") }] });
  const ticks = [];
  const result = await playExport({ manifest, loadFile: async () => content, onFrame: f => ticks.push(f.tick) });
  assert.equal(result.frames, 1);
  assert.deepEqual(ticks, [0]);
  await assert.rejects(playExport({ manifest, loadFile: async () => new Uint8Array([1]), onFrame: () => {} }), /integrity/);
});
