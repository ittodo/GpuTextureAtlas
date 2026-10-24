import fs from 'node:fs';
import assert from 'node:assert/strict';
const output = process.argv[2];
assert(output, 'Pass the profile JSON path after its process exits.');
const bytes = fs.readFileSync(output + '.snapshot.bin');
const number = offset => {
  const value = bytes.readBigUInt64LE(offset);
  assert(value <= BigInt(Number.MAX_SAFE_INTEGER));
  return Number(value);
};
assert.equal(bytes.readBigUInt64LE(0), 0x41544C4153434C52n, 'snapshot magic');
assert.equal(number(8), 1, 'snapshot version');
const fields = ['pid', 'sequence', 'typeCount', 'gcCount', 'inGc', 'collisions', 'boundsFailures', 'nestedGc', 'capacityErrors', 'nameErrors'];
const snapshot = Object.fromEntries(fields.map((key, i) => [key, number(16 + i * 8)]));
assert.equal(snapshot.sequence % 2, 0, 'Process exited inside a counter update.');
assert.equal(snapshot.inGc, 0, 'Process exited during GC.');
for (const key of ['collisions', 'boundsFailures', 'nestedGc', 'capacityErrors', 'nameErrors']) assert.equal(snapshot[key], 0, key);
assert(snapshot.typeCount > 0 && snapshot.typeCount <= 4096);
const data = JSON.parse(fs.readFileSync(output, 'utf8'));
assert.equal(data.pid, snapshot.pid);
assert.equal(data.gc.length, snapshot.gcCount, 'All GC reports must be committed.');
const rows = [];
for (let i = 0; i < snapshot.typeCount; i++) {
  const start = 96 + i * 4136;
  const type = bytes.subarray(start + 40, start + 4136).toString('utf8').split('\0')[0];
  const allocated = number(start + 8), collected = number(start + 16);
  rows.push({ type, allocated, collected, remainingTracked: allocated - collected, allocatedBytes: number(start + 24), collectedBytes: number(start + 32) });
}
const total = (list, key) => list.reduce((sum, row) => sum + row[key], 0);
assert.equal(total(rows, 'collected'), total(data.gc, 'collected'));
assert.equal(total(rows, 'collectedBytes'), total(data.gc, 'collectedBytes'));
data.snapshotAfterProcessExit = snapshot;
data.captureEnd = 'process-exit';
data.types = rows.sort((a, b) => b.allocated - a.allocated);
fs.writeFileSync(output, JSON.stringify(data) + '\n');
console.log(JSON.stringify({ captureEnd: data.captureEnd, clrShutdownCallback: data.shutdown, types: rows.length, gcCycles: data.gc.length, allocated: total(rows, 'allocated'), collected: total(rows, 'collected') }));
