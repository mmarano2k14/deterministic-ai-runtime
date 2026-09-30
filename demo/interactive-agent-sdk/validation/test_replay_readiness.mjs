import { test } from 'node:test';
import assert from 'node:assert/strict';
import { replayAfterSnapshotVisibility as run } from '../typescript/dist/replay-readiness.js';

const id = 'published-test-execution';
const response = (fields = {}) => ({ executionId: id, succeeded: true, deterministic: true, ...fields });
const missing = (fields = {}) => response({ succeeded: false, deterministic: false, failureReason: 'Snapshot not found.', ...fields });
async function sequence(values, completed = true) {
  let calls = 0;
  const result = await run(id, completed, async () => values[Math.min(calls++, values.length - 1)], { timeoutMs: 1_000, pollMs: 1 });
  return { result, calls };
}

test('immediate success is returned unchanged', async () => {
  const item = response();
  const { result, calls } = await sequence([item]);
  assert.equal(result.response, item);
  assert.equal(calls, 1);
  assert.equal(result.snapshotMissingCount, 0);
});
test('only missing snapshot responses are retried to actual success', async () => {
  const item = response();
  const { result, calls } = await sequence([missing(), missing(), item]);
  assert.equal(result.response, item);
  assert.equal(calls, 3);
  assert.equal(result.snapshotMissingCount, 2);
});
test('other failure is returned, not retried', async () => {
  const item = response({ succeeded: false, deterministic: false, failureReason: 'Snapshot invalid.' });
  const { result, calls } = await sequence([item, response()]);
  assert.equal(result.response, item);
  assert.equal(calls, 1);
});
test('determinism failure is not retried', async () => {
  const item = response({ deterministic: false, failureReason: 'Fingerprint mismatch.' });
  const { result, calls } = await sequence([item, response()]);
  assert.equal(result.response, item);
  assert.equal(calls, 1);
});
test('noncompleted execution does not wait', async () => {
  const item = missing();
  const { result, calls } = await sequence([item, response()], false);
  assert.equal(result.response, item);
  assert.equal(calls, 1);
});
test('nonexact missing reason is not retried', async () => {
  const item = missing({ failureReason: 'Snapshot not found. Details' });
  const { result, calls } = await sequence([item, response()]);
  assert.equal(result.response, item);
  assert.equal(calls, 1);
});
test('changed ExecutionId fails immediately', async () => {
  await assert.rejects(sequence([missing({ executionId: 'other' })]), /changed the durable ExecutionId/);
});
test('transport failure propagates once', async () => {
  const error = new Error('unauthorized transport');
  let calls = 0;
  await assert.rejects(run(id, true, async () => { calls++; throw error; }), (caught) => caught === error);
  assert.equal(calls, 1);
});
test('permanent absence fails at deadline', async () => {
  await assert.rejects(run(id, true, async () => missing(), { timeoutMs: 25, pollMs: 5 }), /readiness timed out/);
});
test('inflight deadline is propagated through AbortSignal', async () => {
  let cancelled = false;
  await assert.rejects(run(id, true, async (signal) => new Promise((resolve, reject) => {
    signal.addEventListener('abort', () => { cancelled = true; reject(signal.reason); }, { once: true });
  }), { timeoutMs: 25 }), /readiness timed out/);
  assert.equal(cancelled, true);
});
test('uncooperative async transport cannot make the wait unbounded', async () => {
  await assert.rejects(run(id, true, async () => new Promise(() => {}), { timeoutMs: 25 }), /readiness timed out/);
});
test('caller cancellation propagates unchanged', async () => {
  const controller = new AbortController();
  const reason = new Error('caller cancelled');
  const result = run(id, true, async () => new Promise(() => {}), { signal: controller.signal });
  controller.abort(reason);
  await assert.rejects(result, (caught) => caught === reason);
});
test('already cancelled caller does not make any request', async () => {
  const controller = new AbortController();
  controller.abort();
  let calls = 0;
  await assert.rejects(run(id, true, async () => { calls++; return response(); }, { signal: controller.signal }));
  assert.equal(calls, 0);
});
test('invalid durations fail before calling transport', async () => {
  for (const options of [{ timeoutMs: 0 }, { pollMs: 0 }, { timeoutMs: NaN }]) {
    await assert.rejects(run(id, true, async () => { assert.fail('callback must not run'); }, options));
  }
});
test('each readiness attempt is traced', async () => {
  const entries = [];
  const values = [missing(), response()];
  let index = 0;
  const result = await run(id, true, async () => values[index++], { pollMs: 1, trace: (line) => entries.push(line) });
  assert.equal(result.attempts, 2);
  assert.match(entries[0], /Outcome=SnapshotPending/);
  assert.match(entries[1], /Outcome=Ready/);
});
