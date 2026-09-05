import {
  GhosttyTerminalSurface,
  type GhosttyTerminalFont,
} from "./ghostty/surface";
import type { GhosttyTheme } from "./ghostty/core";
import { resolveCommandShortcut } from "./shortcuts";

interface WebViewBridge {
  addEventListener(type: "message", listener: (event: MessageEvent) => void): void;
  postMessage(message: unknown): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
    pistationTerminalHost?: {
      readonly messages: readonly unknown[];
      postMessage(message: unknown): void;
    };
  }
}

type HostMessage =
  | { type: "write"; text: string }
  | { type: "reset"; text: string }
  | { type: "paste"; text: string }
  | { type: "selectAll" }
  | { type: "search"; query: string; caseSensitive: boolean; wholeWord: boolean }
  | { type: "navigateSearch"; direction: 1 | -1 }
  | { type: "clearSearch" }
  | { type: "theme"; theme: GhosttyTheme; font?: GhosttyTerminalFont }
  | { type: "commandGestures"; gestures: string[] }
  | { type: "focus" };

const browserBridge = window.chrome?.webview;
const developmentMessages: unknown[] = [];
const developmentListeners = new Set<(event: MessageEvent) => void>();
const bridge: WebViewBridge = browserBridge ?? {
  addEventListener(_type, listener) {
    developmentListeners.add(listener);
  },
  postMessage(message) {
    developmentMessages.push(message);
    window.dispatchEvent(new CustomEvent("pistation-terminal-message", { detail: message }));
  },
};
function requireElement(selector: string): HTMLElement {
  const element = document.querySelector<HTMLElement>(selector);
  if (!element) throw new Error(`PiStation terminal element ${selector} was not found`);
  return element;
}

const mount = requireElement("#terminal");
const accessibleOutput = requireElement("#terminal-accessible-output");

if (!browserBridge) {
  window.pistationTerminalHost = {
    messages: developmentMessages,
    postMessage(message) {
      const event = new MessageEvent("message", { data: message });
      for (const listener of developmentListeners) listener(event);
    },
  };
}

const post = (message: unknown): void => bridge.postMessage(message);
let surface: GhosttyTerminalSurface | null = null;
let accessibleFrame = 0;
let commandGestures = new Set<string>();

function publishTerminalState(): void {
  if (accessibleFrame !== 0) window.cancelAnimationFrame(accessibleFrame);
  accessibleFrame = window.requestAnimationFrame(() => {
    accessibleFrame = 0;
    if (!surface) return;
    const text = surface.getAccessibleText();
    accessibleOutput.textContent = text;
    post({
      type: "state",
      text,
      selection: surface.getSelection(),
      mouseTracking: surface.isMouseTracking(),
    });
  });
}

function requestContextMenu(x: number, y: number): void {
  const selection = surface?.getSelection() ?? "";
  post({ type: "contextMenu", x, y, selection });
}

function isHostMessage(value: unknown): value is HostMessage {
  return typeof value === "object" && value !== null && "type" in value;
}

async function initialize(): Promise<void> {
  const initialTheme: GhosttyTheme = {
    foreground: { r: 212, g: 212, b: 212 },
    background: { r: 24, g: 24, b: 27 },
    cursor: { r: 124, g: 156, b: 255 },
    selectionBackground: "rgba(98, 126, 234, 0.35)",
  };

  surface = await GhosttyTerminalSurface.create(mount, {
    theme: initialTheme,
    font: { family: "Cascadia Mono, Consolas", size: 12 },
    onData: (data) => post({ type: "data", data }),
    onResize: (columns, rows) => {
      post({ type: "resize", columns, rows });
      publishTerminalState();
    },
    onSelectionChange: publishTerminalState,
    onSearchStateChange: ({ query, activeIndex, total }) =>
      post({ type: "searchState", query, activeIndex, total }),
    beforeKey: (event) => {
      const commandGesture = resolveCommandShortcut(event, commandGestures);
      if (commandGesture) {
        event.preventDefault();
        event.stopPropagation();
        post({ type: "shortcut", gesture: commandGesture });
        return false;
      }
      const isKeyboardMenu = event.key === "ContextMenu" || (event.key === "F10" && event.shiftKey);
      if (!isKeyboardMenu) return true;
      event.preventDefault();
      event.stopPropagation();
      const anchor = surface?.getSelectionEndClientRect();
      requestContextMenu(anchor?.right ?? 12, anchor?.bottom ?? 12);
      return false;
    },
    onLinkActivate: (text) => post({ type: "link", text }),
    onContextMenu: (event) => {
      event.preventDefault();
      requestContextMenu(event.clientX, event.clientY);
    },
  });

  bridge.addEventListener("message", (event) => {
    const message: unknown = event.data;
    if (!surface || !isHostMessage(message)) return;
    switch (message.type) {
      case "write":
        surface.write(message.text);
        publishTerminalState();
        break;
      case "reset":
        surface.resetAndWrite(message.text);
        publishTerminalState();
        break;
      case "paste":
        void surface.pasteFromClipboard(async () => message.text);
        break;
      case "selectAll":
        surface.selectAll();
        publishTerminalState();
        break;
      case "search":
        surface.setSearch(message.query, {
          caseSensitive: message.caseSensitive,
          wholeWord: message.wholeWord,
        });
        break;
      case "navigateSearch":
        surface.navigateSearch(message.direction);
        break;
      case "clearSearch":
        surface.clearSearch();
        break;
      case "theme":
        surface.setTheme(message.theme);
        if (message.font) {
          void surface.setFont(message.font).then((state) => {
            if (state) post({ type: "fontState", ...state });
          });
        }
        break;
      case "commandGestures":
        commandGestures = new Set(message.gestures.slice(0, 128));
        break;
      case "focus":
        surface.focus();
        break;
    }
  });

  post({ type: "ready" });
}

void initialize().catch((error: unknown) => {
  const message = error instanceof Error ? error.message : "Unknown terminal initialization error";
  post({ type: "error", message });
});
