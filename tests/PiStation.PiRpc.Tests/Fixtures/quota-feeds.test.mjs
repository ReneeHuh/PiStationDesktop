import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname, basename } from "node:path";
import { publishQuotaFeed, registerQuotaFeeds } from "../../../src/PiStation.App/PiExtensions/pistation-quotas.mjs";

function clean(root) {
  const target = resolve(root);
  assert.equal(dirname(target), resolve(tmpdir()));
  assert.ok(basename(target).startsWith("pistation-quota-"));
  rmSync(target, { recursive: true, force: true });
}

test("Pi quota publisher merges sparse windows, retains failed probes and clears unsupported accounts", () => {
  const root = mkdtempSync(join(tmpdir(), "pistation-quota-"));
  const now = new Date("2026-09-10T12:00:00Z");
  const window = { id: "session", kind: "session", label: "Session", usedPercent: 20, resetsAt: "2026-09-10T14:00:00Z", windowDurationMins: 300 };
  const account = { id: "account", provider: "pi-fixture", label: "Fixture", windows: [window] };
  const request = { id: "fixture", label: "Pi fixture", accounts: [account] };
  const read = () => JSON.parse(readFileSync(join(root, readdirSync(root)[0]), "utf8"));
  try {
    publishQuotaFeed(root, { ...request, credential: "must never persist" }, now);
    assert.equal(read().accounts[0].windows[0].usedPercent, 20);
    assert.equal(JSON.stringify(read()).includes("must never persist"), false);
    publishQuotaFeed(root, { ...request, mode: "update", accounts: [{ ...account, windows: [{ id: "session", kind: "session", label: "Session", usedPercent: 50 }] }] }, now);
    assert.equal(read().accounts[0].windows[0].resetsAt, new Date(window.resetsAt).toISOString());
    assert.equal(read().accounts[0].windows[0].windowDurationMins, 300);
    publishQuotaFeed(root, { ...request, accounts: [{ ...account, unavailable: "probeFailed", windows: [] }] }, new Date(now.getTime() + 60000));
    assert.equal(read().accounts[0].windows[0].usedPercent, 50);
    assert.equal(read().accounts[0].checkedAt, now.toISOString());
    publishQuotaFeed(root, { ...request, accounts: [{ ...account, unavailable: "unsupported", windows: [] }] }, new Date(now.getTime() + 120000));
    assert.deepEqual(read().accounts[0].windows, []);
    publishQuotaFeed(root, { ...request, mode: "update" }, new Date(now.getTime() + 180000));
    assert.equal(read().accounts[0].unavailable, "unsupported");
    publishQuotaFeed(root, request, new Date(now.getTime() + 240000));
    assert.equal(read().accounts[0].unavailable, null);
    publishQuotaFeed(root, { id: request.id, remove: true }, now);
    assert.deepEqual(readdirSync(root), []);
  } finally { clean(root); }
});

test("Pi quota publisher rejects malformed/duplicate data without overwriting a successful snapshot", () => {
  const root = mkdtempSync(join(tmpdir(), "pistation-quota-"));
  try {
    const account = { id: "a", provider: "fixture", label: "Fixture", windows: [] };
    const request = { id: "../safe-hashed-name", label: "Fixture", accounts: [account] };
    publishQuotaFeed(root, request);
    assert.match(readdirSync(root)[0], /^[a-f0-9]{64}\.json$/);
    assert.throws(() => publishQuotaFeed(root, { ...request, accounts: [account, account] }));
    assert.throws(() => publishQuotaFeed(root, { ...request, accounts: [{ ...account, windows: [{ id: "a", kind: "session", label: "Session", usedPercent: NaN }] }] }));
    assert.throws(() => publishQuotaFeed(root, { ...request, checkedAt: "2099-01-01T00:00:00Z" }));
    assert.equal(readdirSync(root).length, 1);
    assert.deepEqual(JSON.parse(readFileSync(join(root, readdirSync(root)[0]), "utf8")).accounts[0].windows, []);
  } finally { clean(root); }
});

test("Pi event registration exposes a metadata-only acknowledgement and unsubscribes on shutdown", () => {
  let handler; let shutdown; let unsubscribed = false;
  registerQuotaFeeds({ events: { on: (name, callback) => { assert.equal(name, "pistation:usage-limits:v1"); handler = callback; return () => { unsubscribed = true; }; } },
    on: (name, callback) => { assert.equal(name, "session_shutdown"); shutdown = callback; } });
  let response;
  handler({ id: "invalid\n", reply: value => { response = value; } });
  assert.equal(response.success, false); assert.equal(response.error.includes("invalid\n"), false);
  shutdown(); assert.equal(unsubscribed, true);
});
