// Text compatibility surface for Pi TUI components. Factories stay inside Pi;
// only bounded rendered text and explicit input cross the existing RPC channel.
export function createDesktopUI(base, sdk, session) {
  let disposed = false;
  let activeDone;
  let activeRefresh;
  let editorFactory;
  const autocompleteFactories = [];
  let columns = 100, rows = 40;
  const widgets = new Map();
  const widgetKeys = new Set();
  const statusKeys = new Set();
  const listeners = new Set();
  let abort = new AbortController();
  const clean = value => String(value ?? "").replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, "").replace(/\x1b\][^\x07]*(?:\x07|\x1b\\)/g, "").slice(0, 12000);
  const lines = component => component.render(columns).slice(0, 120).map(clean).join("\n").slice(0, 12000).split("\n");
  const render = () => {
    if (disposed) return;
    activeRefresh?.();
    for (const [key, component] of widgets) {
      try { base.setWidget(key, lines(component)); }
      catch (error) { base.notify("Extension component failed: " + clean(error.message), "error"); }
    }
  };
  const tui = { terminal: { get columns() { return columns; }, get rows() { return rows; } }, requestRender: render,
    setFocus(component) { if (component) component.focused = true; },
    requestFullRedraw: render, invalidate: render, get size() { return { columns, rows }; } };
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
      const expires = Date.now() + 10 * 60 * 1000;
      const done = value => { if (finished) return; finished = true; result = value; completion.abort(); };
      activeDone = done;
      try {
        component = await factory(tui, base.theme, sdk.createDesktopKeybindings?.() ?? sdk.KeybindingsManager.create(), done);
        if (!component || typeof component.render !== "function") throw new Error("Extension UI must return a renderable component.");
        component.focused = true;
        options?.onHandle?.({ hide: () => done(undefined), show: render, isVisible: () => !finished, setHidden: value => { if (value) done(undefined); } });
        while (!finished && !disposed && Date.now() < expires) {
          const refresh = new AbortController();
          const frame = lines(component).join("\n");
          activeRefresh = () => { if (lines(component).join("\n") !== frame) refresh.abort(); };
          const value = await base.input("Extension component\n" + frame,
            `[pistation:component:${componentId}]`, { signal: AbortSignal.any([abort.signal, completion.signal, refresh.signal]), timeout: Math.max(1, expires - Date.now()) });
          activeRefresh = undefined;
          if (finished || disposed) break;
          if (value === undefined && refresh.signal.aborted) {
            base.setStatus("pistation-component-closed", componentId);
            // Coalesce animation invalidations instead of creating an unbounded stream of questions.
            await new Promise(resolve => setTimeout(resolve, 500));
            continue;
          }
          if (value === undefined) { done(undefined); break; }
          let input = keys[value] ?? (/^Ctrl\+[A-Z]$/i.test(value) ? String.fromCharCode(value.at(-1).toUpperCase().charCodeAt(0) - 64) : value);
          if (value.startsWith("[pistation:event]")) {
            const event = JSON.parse(value.slice(17));
            if (event.type === "resize" && Number.isInteger(event.columns) && event.columns >= 20 && event.columns <= 240 &&
                Number.isInteger(event.rows) && event.rows >= 5 && event.rows <= 120) {
              columns = event.columns; rows = event.rows; component.invalidate?.(); render(); continue;
            }
            if (event.type === "mouse" && Number.isInteger(event.column) && event.column >= 1 && event.column <= columns &&
                Number.isInteger(event.row) && event.row >= 1 && event.row <= 120 && [0, 1, 2, 64, 65].includes(event.button))
              input = `\x1b[<${event.button};${event.column};${event.row}${event.release ? "m" : "M"}`;
            else throw new Error("Invalid native component event.");
          }
          let handled = false;
          for (const listener of listeners) {
            const response = listener(input);
            if (response?.data !== undefined) input = response.data;
            if (response?.consume) { handled = true; break; }
          }
          if (!handled) component.handleInput?.(input);
        }
        return result;
      } finally {
        // Pi's RPC dialog abort only retires its response handler. Retire the
        // correlated native question as well, including asynchronous done().
        base.setStatus("pistation-component-closed", componentId);
        activeDone = undefined;
        activeRefresh = undefined;
        component?.dispose?.();
      }
    },
    setEditorComponent(factory) { editorFactory = factory; },
    getEditorComponent() { return editorFactory; },
    addAutocompleteProvider(factory) {
      if (typeof factory !== "function" || autocompleteFactories.length >= 32) throw new Error("At most 32 autocomplete providers are supported.");
      autocompleteFactories.push(factory);
    },
  };
  return { context, async edit(text = "") {
    const result = await context.custom((surface, theme, keybindings, done) => {
      const editorTheme = { borderColor: value => value, selectList: {
        selectedPrefix: value => value, selectedText: value => value, description: value => value, scrollInfo: value => value, noMatch: value => value,
      } };
      const editor = editorFactory ? editorFactory(surface, editorTheme, keybindings) : sdk.createDesktopEditor?.(surface, editorTheme, keybindings);
      if (!editor || typeof editor.setText !== "function") throw new Error("The extension editor must implement Pi's EditorComponent contract.");
      editor.setText(text); editor.onSubmit = value => done(value);
      let provider = sdk.createDesktopAutocomplete?.() ?? { getSuggestions: async () => null, applyCompletion: () => { throw new Error("No completion selected."); } };
      for (const factory of autocompleteFactories) provider = factory(provider);
      editor.setAutocompleteProvider?.(provider);
      return editor;
    });
    // Native composer proposals remain explicit; editing never starts a model turn.
    if (typeof result === "string") base.setEditorText(result);
    return result;
  }, reset() {
    abort.abort(); abort = new AbortController(); activeDone?.(undefined);
    for (const [key, component] of widgets) { component.dispose?.(); base.setWidget(key, undefined); }
    for (const key of widgetKeys) base.setWidget(key, undefined);
    for (const key of statusKeys) base.setStatus(key, undefined);
    widgets.clear(); listeners.clear(); widgetKeys.clear(); statusKeys.clear();
    editorFactory = undefined; autocompleteFactories.length = 0;
  }, dispose() {
    if (disposed) return;
    disposed = true; abort.abort(); activeDone?.(undefined);
    for (const [key, component] of widgets) { component.dispose?.(); base.setWidget(key, undefined); }
    for (const key of widgetKeys) base.setWidget(key, undefined);
    for (const key of statusKeys) base.setStatus(key, undefined);
    widgets.clear(); listeners.clear(); widgetKeys.clear(); statusKeys.clear();
    editorFactory = undefined; autocompleteFactories.length = 0;
  } };
}
