import { mkdir, writeFile, rename } from 'node:fs/promises';
import { join } from 'node:path';
import { CONTRACT } from './client.mjs';

// Private local handoff. Public API frames and event envelopes stay unchanged.
export async function createStream(directory, metadata, { currentOnly = false, followLatest = false } = {}) {
  await mkdir(directory, { recursive: true });
  const index = { format: 'gearth.renderer-stream.v1', metadata, current_only: currentOnly, follow_latest: followLatest,
    frames: [], events: [], connection: 'Connected' };
  let sequence = 0, revision = 0, tick = -1, pending = [];
  let pageNumber = 0;
  const publish = async () => {
    await writeFile(join(directory, 'index.tmp'), JSON.stringify(index));
    await rename(join(directory, 'index.tmp'), join(directory, 'index.json'));
  };
  return {
    async onEvents(events) {
      let next = sequence;
      for (const event of events) {
        if (event.run_id !== metadata.run_id || event.seq !== ++next)
          throw new Error('Noncontiguous stream events');
      }
      if (!events.length) return;
      const name = `events-${++pageNumber}.json`;
      await writeFile(join(directory, name), JSON.stringify(events));
      pending.push(name);
      sequence = next;
    },
    async onFrame(frame) {
      if (![frame.revision, frame.tick, frame.through_event_sequence].every(Number.isSafeInteger) ||
          frame.tick < 0 || frame.through_event_sequence < 0 || frame.schema_version !== CONTRACT || frame.run_id !== metadata.run_id ||
          frame.snapshot?.run?.id !== metadata.run_id || frame.revision <= revision || frame.tick < tick ||
          (!currentOnly && frame.through_event_sequence !== sequence))
        throw new Error('Invalid stream frame identity, order or event coverage');
      const name = `frame-${frame.revision}.json`;
      await writeFile(join(directory, name), JSON.stringify(frame));
      const previousFrames = index.frames, previousEvents = index.events;
      index.frames = currentOnly ? [name] : [...index.frames, name];
      index.events = [...index.events, ...pending];
      try { await publish(); }
      catch (error) { index.frames = previousFrames; index.events = previousEvents; throw error; }
      pending = []; revision = frame.revision; tick = frame.tick;
    },
    async status(connection, nextMetadata = index.metadata) {
      if (connection === index.connection && JSON.stringify(nextMetadata) === JSON.stringify(index.metadata)) return;
      const previousConnection = index.connection, previousMetadata = index.metadata;
      index.connection = connection; index.metadata = nextMetadata;
      try { if (index.frames.length) await publish(); }
      catch (error) { index.connection = previousConnection; index.metadata = previousMetadata; throw error; }
    },
    revision: () => revision,
  };
}
