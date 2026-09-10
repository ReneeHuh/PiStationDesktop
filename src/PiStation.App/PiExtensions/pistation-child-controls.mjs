import { randomUUID } from "node:crypto";

// Opt-in protocol: children are controlled only by the extension that owns them.
// No PIDs or arbitrary host commands are accepted from a projected result.
export function registerChildControls(pi) {
  const children = new Map();
  const unsubscribe = pi.events.on("pistation:child-controls:v1", request => {
    if (typeof request?.reply !== "function") return;
    if (typeof request.stop !== "function" || children.size >= 64) {
      request.reply({ error: "A stop callback is required; at most 64 external children may be active." }); return;
    }
    const controlId = randomUUID().replaceAll("-", "");
    const child = { stop: request.stop, stopping: undefined };
    children.set(controlId, child);
    request.reply({ controlId, integration: "pistation-external-v1", complete: () => children.delete(controlId) });
  });
  const stop = async id => {
    const child = children.get(id);
    if (!child) return false;
    child.stopping ??= Promise.resolve().then(() => child.stop());
    await child.stopping;
    return true;
  };
  pi.on("session_shutdown", async () => {
    unsubscribe();
    const results = [...children.keys()].map(id => stop(id));
    children.clear();
    await Promise.allSettled(results);
  });
  return { stop, get size() { return children.size; } };
}
