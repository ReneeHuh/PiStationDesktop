// Text compatibility surface for Pi TUI components. Factories stay inside Pi;
// only bounded rendered text and explicit input cross the existing RPC channel.
export function createDesktopUI(base, sdk, session) {
  let disposed = false;
  let activeDone;
  const widgets = new Map();
  const widgetKeys = new Set();
  const statusKeys = new Set();
  const listeners = new Set();
  let abort = new AbortController();
  const clean = value => String(value ?? "").replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, "").replace(/\x1b\][^\x07]*(?:\x07|\x1b\\)/g, "").slice(0, 12000);
  const lines = component => component.render(100).slice(0, 120).map(clean).join("\n").slice(0, 12000).split("\n");
  const render = () => {
    if (disposed) return;
    for (const [key, component] of widgets) {
      try { base.setWidget(key, lines(component)); }
      catch (error) { base.notify("Extension component failed: " + clean(error.message), "error"); }
    }
  };
  const tui = { terminal: { columns: 100, rows: 40 }, requestRender: render,
    setFocus(component) { if (component) component.focused = true; },
    requestFullRedraw: render, invalidate: render, get size() { return { columns: 100, rows: 40 }; } };
  const componentWidget = (key, factory, extra) => {
    widgetKeys.add(key);
    widgets.get(key)?.dispose?.(); widgets.delete(key);
    if (!factory) { base.setWidget(key, undefined); return; }
    const component = factory(tui, base.theme, extra);
    if (!component || typeof component.render !== "function") throw new Error("Extension UI must return a renderable component.");
    widgets.set(key, component); render();
  };
  const keys = { Enter: "\r", Escape: "\x1b", Tab: "\t", Backspace: "\x7f", Up: "\x1b[A", Down: "\x1b[B", Right: "\x1b[C", Left: "\x1b[D", Home: "\x1b[H", End: "\x1b[F", PageUp: "\x1b[5~", PageDown: "\x1b[6~" };
  const context = { ...base,
    setStatus(key, text) {
      if (!key.startsWith("pistation-management:")) statusKeys.add(key);
      base.setStatus(key, text);
    },
    onTerminalInput(handler) { listeners.add(handler); return () => listeners.delete(handler); },
    setWidget(key, content, options) {
      widgetKeys.add(key);
      if (typeof content === "function") componentWidget(key, content);
      else { widgets.get(key)?.dispose?.(); widgets.delete(key); base.setWidget(key, content, options); }
    },
    setHeader(factory) { componentWidget("Extension header", factory); },
    setFooter(factory) { componentWidget("Extension footer", factory, {
      getGitBranch: () => undefined, getExtensionStatuses: () => new Map(), onBranchChange: () => () => {},
    }); },
    setWorkingMessage(message) { statusKeys.add("Extension working"); base.setStatus("Extension working", message); },
    setWorkingVisible(visible) { if (!visible) base.setStatus("Extension working", undefined); },
    setWorkingIndicator(options) { statusKeys.add("Extension working"); base.setStatus("Extension working", options?.frames?.[0]); },
    async custom(factory, options) {
      if (disposed) return undefined;
      if (activeDone) throw new Error("Close the active extension component first.");
      let finished = false, result, component;
      const componentId = crypto.randomUUID().replaceAll("-", "");
      const completion = new AbortController();
      const done = value => { if (finished) return; finished = true; result = value; completion.abort(); };
      activeDone = done;
      try {
        component = await factory(tui, base.theme, sdk.createDesktopKeybindings?.() ?? sdk.KeybindingsManager.create(), done);
        if (!component || typeof component.render !== "function") throw new Error("Extension UI must return a renderable component.");
        component.focused = true;
        options?.onHandle?.({ hide: () => done(undefined), show: render, isVisible: () => !finished, setHidden: value => { if (value) done(undefined); } });
        while (!finished && !disposed) {
          const value = await base.input("Extension component\n" + lines(component).join("\n"),
            `[pistation:component:${componentId}]`, { signal: AbortSignal.any([abort.signal, completion.signal]), timeout: 10 * 60 * 1000 });
          if (finished || disposed) break;
          if (value === undefined) { done(undefined); break; }
          const input = keys[value] ?? (/^Ctrl\+[A-Z]$/i.test(value) ? String.fromCharCode(value.at(-1).toUpperCase().charCodeAt(0) - 64) : value);
          let handled = false;
          for (const listener of listeners) { const response = listener(input); if (response?.consume) handled = true; }
          if (!handled) component.handleInput?.(input);
        }
        return result;
      } finally {
        // Pi's RPC dialog abort only retires its response handler. Retire the
        // correlated native question as well, including asynchronous done().
        base.setStatus("pistation-component-closed", componentId);
        activeDone = undefined;
        component?.dispose?.();
      }
    },
    setEditorComponent() { base.notify("This extension replaces Pi's terminal editor. PiStation uses its native composer; use custom() for interactive components.", "warning"); },
    addAutocompleteProvider() { base.notify("Terminal autocomplete providers require Pi's terminal editor.", "warning"); },
  };
  return { context, reset() {
    abort.abort(); abort = new AbortController(); activeDone?.(undefined);
    for (const [key, component] of widgets) { component.dispose?.(); base.setWidget(key, undefined); }
    for (const key of widgetKeys) base.setWidget(key, undefined);
    for (const key of statusKeys) base.setStatus(key, undefined);
    widgets.clear(); listeners.clear(); widgetKeys.clear(); statusKeys.clear();
  }, dispose() {
    if (disposed) return;
    disposed = true; abort.abort(); activeDone?.(undefined);
    for (const [key, component] of widgets) { component.dispose?.(); base.setWidget(key, undefined); }
    for (const key of widgetKeys) base.setWidget(key, undefined);
    for (const key of statusKeys) base.setStatus(key, undefined);
    widgets.clear(); listeners.clear(); widgetKeys.clear(); statusKeys.clear();
  } };
}
