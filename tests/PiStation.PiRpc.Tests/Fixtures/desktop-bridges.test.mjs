import { test } from "node:test";
import assert from "node:assert/strict";
import { createDesktopUI } from "../../../src/PiStation.App/PiExtensions/pistation-ui.mjs";
import { registerChildControls } from "../../../src/PiStation.App/PiExtensions/pistation-child-controls.mjs";
import { loginDesktopProvider } from "../../../src/PiStation.App/PiExtensions/pistation-auth.mjs";

test("component resize and mouse coordinates reach the factory and listeners can transform input", async () => {
  const input = ['[pistation:event]{"type":"resize","columns":80,"rows":25}', '[pistation:event]{"type":"mouse","button":0,"column":4,"row":3}', "Enter"];
  const widths = [], received = [];
  const ui = createDesktopUI({ theme: {}, setWidget() {}, setStatus() {}, notify() {}, input: async () => input.shift() }, { createDesktopKeybindings: () => ({}) });
  ui.context.onTerminalInput(data => data === "\r" ? { data: "submitted" } : undefined);
  const result = await ui.context.custom((tui, _theme, _keys, done) => ({
    render: width => { widths.push(width); assert.equal(width, tui.terminal.columns); return ["cell grid"]; },
    invalidate() {}, handleInput: data => { received.push(data); if (data === "submitted") done("ok"); },
  }));
  assert.equal(result, "ok"); assert.deepEqual(widths, [100, 80, 80]);
  assert.deepEqual(received, ["\x1b[<0;4;3M", "submitted"]); ui.dispose();
});

test("extension editor uses stacked autocomplete and returns a composer proposal without submitting a turn", async () => {
  let suggestions, proposal, disposed = 0, calls = 0;
  const ui = createDesktopUI({ theme: {}, setWidget() {}, setStatus() {}, notify() {}, setEditorText: value => { proposal = value; },
    input: async (_title, _hint, { signal }) => ++calls === 1 ? "Tab" : new Promise(resolve => signal.addEventListener("abort", () => resolve(undefined), { once: true })) }, { createDesktopKeybindings: () => ({}) });
  const factory = () => ({ text: "", setText(value) { this.text = value; }, render() { return [this.text]; },
    setAutocompleteProvider(provider) { suggestions = provider; },
    handleInput() { void suggestions.getSuggestions([this.text], 0, this.text.length, { signal: new AbortController().signal }).then(result => {
      this.text = suggestions.applyCompletion([this.text], 0, this.text.length, result.items[0], result.prefix).lines.join("\n"); this.onSubmit(this.text);
    }); }, dispose() { disposed++; } });
  ui.context.setEditorComponent(factory); assert.equal(ui.context.getEditorComponent(), factory);
  ui.context.addAutocompleteProvider(current => ({ ...current, getSuggestions: async () => ({ prefix: "he", items: [{ value: "hello" }] }),
    applyCompletion: (_lines, _row, _column, item) => ({ lines: [item.value], cursorLine: 0, cursorCol: 5 }) }));
  assert.equal(await ui.edit("he"), "hello"); assert.equal(proposal, "hello"); assert.equal(disposed, 1);
  ui.reset(); assert.equal(ui.context.getEditorComponent(), undefined); ui.dispose();
});

test("asynchronous invalidation refreshes the component without treating refresh as cancellation", async () => {
  let calls = 0, surface, done;
  const ui = createDesktopUI({ theme: {}, setWidget() {}, setStatus() {}, notify() {}, input: async (_title, _hint, { signal }) => {
    calls++;
    if (calls === 1) { setTimeout(() => surface.requestRender(), 0); return new Promise(resolve => signal.addEventListener("abort", () => resolve(undefined), { once: true })); }
    done("refreshed"); return undefined;
  } }, { createDesktopKeybindings: () => ({}) });
  assert.equal(await ui.context.custom((tui, _theme, _keys, finish) => { surface = tui; done = finish; return { render: () => ["render " + calls] }; }), "refreshed");
  assert.equal(calls, 2); ui.dispose();
});

test("authentication covers secret prompts, selector IDs and HTTPS device/browser notifications", async () => {
  const opened = [], statuses = [], hints = [];
  const runtime = { login: async (provider, kind, callbacks) => {
    assert.equal(provider, "isolated"); assert.equal(kind, "oauth");
    assert.equal(await callbacks.prompt({ type: "select", message: "Choose", options: [{ id: "tenant-id", label: "Tenant" }] }), "tenant-id");
    assert.equal(await callbacks.prompt({ type: "input", message: "Code" }), "dummy-secret");
    callbacks.notify({ type: "auth_url", url: "file:///unsafe" });
    callbacks.notify({ type: "auth_url", url: "https://user:secret@example.test/auth" });
    callbacks.notify({ type: "device_code", verificationUri: "https://example.test/device", userCode: "TEST" });
  } };
  await loginDesktopProvider(runtime, "isolated", "oauth", { select: async () => "Tenant", input: async (_message, hint) => { hints.push(hint); return "dummy-secret"; },
    setStatus: (_key, value) => statuses.push(value) }, url => opened.push(url));
  assert.deepEqual(hints, ["[pistation:secret]"]); assert.deepEqual(opened, ["https://example.test/device"]);
  assert.equal(statuses.some(value => value?.includes("dummy-secret")), false);
});

test("authentication cancellation, provider errors and timeout never expose credential text", async () => {
  const ui = { input: async () => undefined, setStatus() {} };
  await assert.rejects(loginDesktopProvider({ login: async (_id, _kind, callbacks) => callbacks.prompt({ type: "input", message: "Key" }) }, "isolated", "api_key", ui, () => {}), /cancelled/);
  await assert.rejects(loginDesktopProvider({ login: async () => { throw new Error("dummy-secret echoed"); } }, "isolated", "api_key", ui, () => {}), error => !error.message.includes("dummy-secret") && error.message.includes("failed"));
  await assert.rejects(loginDesktopProvider({ login: async (_id, _kind, { signal }) => new Promise((_, reject) => signal.addEventListener("abort", () => reject(new Error("dummy-secret")), { once: true })) }, "isolated", "oauth", ui, () => {}, 10), /timed out/);
});

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
