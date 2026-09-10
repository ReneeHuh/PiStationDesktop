// Import through Pi's extension loader so bundled/native installations share the
// exact SDK classes used by their CLI, rather than a second unbundled SDK copy.
import * as sdk from "@earendil-works/pi-coding-agent";
import { getKeybindings } from "@earendil-works/pi-tui";
import { createDesktopUI } from "./pistation-ui.mjs";

export default function () {
  const key = Symbol.for("pistation.sdk");
  const installed = Symbol.for("pistation.sdk.bindings-installed");
  const prototype = sdk.AgentSession.prototype as any;
  if (prototype[installed]) return;
  const bind = prototype.bindExtensions;
  if (typeof bind !== "function") throw new Error("This Pi SDK does not support desktop extension bindings.");
  prototype[installed] = true;
  prototype.bindExtensions = async function (bindings: any) {
    // Child SDK sessions can bind their own contexts; only augment the RPC owner.
    if (bindings.mode !== "rpc" || !bindings.uiContext) return bind.call(this, bindings);
    (globalThis as any)[key]?.ui?.dispose();
    const desktop = createDesktopUI(bindings.uiContext, { ...sdk,
      createDesktopKeybindings: () => (sdk as any).KeybindingsManager?.create?.() ?? getKeybindings() }, this);
    (globalThis as any)[key] = { session: this, ui: desktop, version: 1 };
    return bind.call(this, { ...bindings, uiContext: desktop.context });
  };
}
