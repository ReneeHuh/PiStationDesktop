import { test } from "node:test";
import assert from "node:assert/strict";
import { createDesktopUI } from "../../../src/PiStation.App/PiExtensions/pistation-ui.mjs";
import { registerChildControls } from "../../../src/PiStation.App/PiExtensions/pistation-child-controls.mjs";

test("custom component handles keys, returns its result and disposes; widgets reset", async () => {
  const rendered = new Map(); let disposed = 0; const input = ["Down", "Enter"];
  const ui = createDesktopUI({ theme: {}, setWidget: (key, lines) => rendered.set(key, lines),
    notify: () => {}, setStatus: () => {}, input: async () => input.shift() }, { KeybindingsManager: { create: () => ({}) } });
  ui.context.setHeader(() => ({ render: () => ["\x1b[31mHeader\x1b[0m"], dispose: () => disposed++ }));
  assert.deepEqual(rendered.get("Extension header"), ["Header"]);
  const result = await ui.context.custom((_tui, _theme, _keys, done) => ({
    render: () => ["Choose"], handleInput: key => { if (key === "\r") done("selected"); else assert.equal(key, "\x1b[B"); }, dispose: () => disposed++,
  }));
  assert.equal(result, "selected"); ui.reset(); assert.equal(disposed, 2);
  assert.equal(rendered.get("Extension header"), undefined);
  ui.dispose();
});

test("external control stops only its owner, rejects stale handles and cancels on shutdown", async () => {
  let register, shutdown, unsubscribed = false;
  const controls = registerChildControls({ events: { on: (_channel, fn) => { register = fn; return () => { unsubscribed = true; }; } },
    on: (_event, fn) => { shutdown = fn; } });
  let first = 0, second = 0, a, b;
  register({ stop: () => first++, reply: value => { a = value; } });
  register({ stop: () => second++, reply: value => { b = value; } });
  assert.match(a.controlId, /^[a-f0-9]{32}$/); assert.notEqual(a.controlId, b.controlId);
  assert.equal(await controls.stop(a.controlId), true); assert.equal(first, 1); assert.equal(second, 0);
  await controls.stop(a.controlId); assert.equal(first, 1);
  a.complete(); assert.equal(await controls.stop(a.controlId), false);
  await shutdown(); assert.equal(second, 1); assert.equal(controls.size, 0); assert.equal(unsubscribed, true);
  assert.equal(await controls.stop(b.controlId), false);
});

test("asynchronous component completion retires pending input and bounds rendered text", async () => {
  let closeId, placeholder, disposed = false;
  const statuses = new Map();
  const ui = createDesktopUI({ theme: {}, setWidget: () => {}, notify: () => {},
    setStatus: (key, value) => { statuses.set(key, value); if (key === "pistation-component-closed") closeId = value; },
    input: (title, hint, { signal }) => {
      assert.equal(title.length, "Extension component\n".length + 12000);
      placeholder = hint;
      return new Promise(resolve => signal.addEventListener("abort", () => resolve(undefined), { once: true }));
    } }, { createDesktopKeybindings: () => ({}) });
  const result = await ui.context.custom((_tui, _theme, _keys, done) => {
    setTimeout(() => { done("finished asynchronously"); done("must not replace result"); }, 10);
    return { render: () => Array(200).fill("x".repeat(1000)), dispose: () => { disposed = true; } };
  });
  assert.equal(result, "finished asynchronously"); assert.equal(disposed, true);
  assert.equal(placeholder, `[pistation:component:${closeId}]`);
  ui.context.setStatus("owned status", "value"); ui.context.setWorkingMessage("busy");
  ui.reset(); assert.equal(statuses.get("owned status"), undefined); assert.equal(statuses.get("Extension working"), undefined);
});
