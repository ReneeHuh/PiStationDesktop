import { createHash, randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const maxBytes = 1024 * 1024;
function text(value, max) {
  if (typeof value !== "string" || !value.length || value.length > max || /[\x00-\x1f\x7f]/.test(value)) throw new Error("Invalid quota metadata.");
  return value;
}
function date(value, fallback) {
  if (value == null) return fallback;
  const millis = typeof value === "string" ? Date.parse(value) : NaN;
  if (!Number.isFinite(millis)) throw new Error("Invalid quota timestamp.");
  return new Date(millis).toISOString();
}
function unique(values, max) {
  if (!Array.isArray(values) || values.length > max || new Set(values.map(v => v?.id)).size !== values.length) throw new Error("Invalid quota collection.");
  return values;
}

// Extensions own upstream authentication and polling. Only quota metadata crosses this bridge.
// A single publisher owns each stable source id; a snapshot replaces its account/window set.
export function publishQuotaFeed(root, request, now = new Date()) {
  if (!root) throw new Error("The desktop quota publisher is unavailable.");
  const id = text(request?.id, 128);
  const path = join(root, createHash("sha256").update(id).digest("hex") + ".json");
  if (request.remove === true) { if (existsSync(path)) unlinkSync(path); return; }
  if (!["snapshot", "update"].includes(request.mode ?? "snapshot")) throw new Error("Invalid quota publication mode.");
  const label = text(request.label, 128);
  const checkedAt = date(request.checkedAt, now.toISOString());
  if (Date.parse(checkedAt) > now.getTime() + 300000) throw new Error("Quota timestamp is in the future.");
  let previous;
  if (existsSync(path) && statSync(path).size <= maxBytes) {
    try { previous = JSON.parse(readFileSync(path, "utf8")); } catch { }
  }
  if (previous?.id !== id || !Array.isArray(previous?.accounts)) previous = undefined;
  if (previous && Date.parse(previous.checkedAt) > Date.parse(checkedAt)) return;
  const update = request.mode === "update";
  const accounts = new Map(update ? previous?.accounts.map(a => [a.id, a]) : []);
  for (const account of unique(request.accounts, 128)) {
    const accountId = text(account.id, 256);
    const old = previous?.accounts.find(a => a.id === accountId);
    const unavailable = account.unavailable ?? null;
    if (![null, "unsupported", "probeFailed"].includes(unavailable)) throw new Error("Invalid quota availability.");
    // Failed probes preserve the last good timestamp/bars. Explicit unsupported clears them.
    if (unavailable === "probeFailed" && old) { accounts.set(accountId, { ...old, unavailable }); continue; }
    if (update && old?.unavailable === "unsupported" && unavailable !== "unsupported") continue;
    const windows = new Map(update && unavailable !== "unsupported" ? old?.windows.map(w => [w.id, w]) : []);
    for (const window of unique(account.windows, 32)) {
      const windowId = text(window.id, 128);
      const previousWindow = windows.get(windowId);
      const kind = text(window.kind, 16);
      if (!["session", "weekly", "monthly", "other"].includes(kind) || !Number.isFinite(window.usedPercent)) throw new Error("Invalid quota window.");
      const duration = window.windowDurationMins ?? previousWindow?.windowDurationMins ?? null;
      if (duration != null && (!Number.isFinite(duration) || duration < 0 || duration > 527040)) throw new Error("Invalid quota duration.");
      windows.set(windowId, { id: windowId, kind, label: text(window.label, 128), usedPercent: Math.max(0, Math.min(100, window.usedPercent)),
        resetsAt: date(window.resetsAt, previousWindow?.resetsAt ?? null), windowDurationMins: duration });
    }
    if (windows.size > 32) throw new Error("Too many quota windows.");
    accounts.set(accountId, { id: accountId, provider: text(account.provider, 80), label: text(account.label, 256),
      checkedAt, windows: unavailable === "unsupported" ? [] : [...windows.values()], plan: account.plan == null ? null : text(account.plan, 100), unavailable });
  }
  if (accounts.size > 128) throw new Error("Too many quota accounts.");
  const bytes = JSON.stringify({ version: 1, id, label, checkedAt, accounts: [...accounts.values()] });
  if (Buffer.byteLength(bytes) > maxBytes) throw new Error("Quota feed exceeds its size limit.");
  mkdirSync(root, { recursive: true });
  const temporary = path + "." + randomUUID() + ".tmp";
  try { writeFileSync(temporary, bytes, { mode: 0o600 }); renameSync(temporary, path); }
  finally { if (existsSync(temporary)) unlinkSync(temporary); }
}

export function registerQuotaFeeds(pi) {
  const unsubscribe = pi.events.on("pistation:usage-limits:v1", request => {
    try { publishQuotaFeed(process.env.PISTATION_QUOTA_ROOT, request); request?.reply?.({ success: true }); }
    catch { request?.reply?.({ success: false, error: "Quota publication failed. Check the version 1 quota metadata contract." }); }
  });
  pi.on("session_shutdown", () => unsubscribe());
}
