import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { randomUUID } from "node:crypto";
import { mkdir, readFile, rename, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { Type } from "typebox";

const PARAMETERS = Type.Object({
  action: Type.Union([
    Type.Literal("status"),
    Type.Literal("open"),
    Type.Literal("resize"),
    Type.Literal("set_appearance"),
    Type.Literal("navigate"),
    Type.Literal("snapshot"),
    Type.Literal("evaluate"),
    Type.Literal("click"),
    Type.Literal("type"),
    Type.Literal("screenshot"),
    Type.Literal("press_key"),
    Type.Literal("scroll"),
    Type.Literal("wait"),
  ]),
  tabId: Type.Optional(Type.String({ minLength: 1, maxLength: 160, description: "Stable tab ID from status/open. Omit to use this agent thread's pinned tab; before the first operation, use its selected tab." })),
  open: Type.Optional(Type.Boolean({ description: "Reveal the preview for this thread (default true); false keeps background work hidden." })),
  reuseExistingTab: Type.Optional(Type.Boolean({ description: "Default true. Set false to create another tab; cannot combine with tabId." })),
  mode: Type.Optional(Type.Union([Type.Literal("fill"), Type.Literal("freeform"), Type.Literal("preset")])),
  width: Type.Optional(Type.Integer({ minimum: 240, maximum: 3840 })),
  height: Type.Optional(Type.Integer({ minimum: 240, maximum: 3840 })),
  preset: Type.Optional(Type.Union([Type.Literal("desktop"), Type.Literal("tablet"), Type.Literal("phone")])),
  orientation: Type.Optional(Type.Union([Type.Literal("portrait"), Type.Literal("landscape")])),
  colorScheme: Type.Optional(Type.Union([Type.Literal("system"), Type.Literal("light"), Type.Literal("dark")])),
  url: Type.Optional(Type.String({ maxLength: 2048 })),
  selector: Type.Optional(Type.String({ maxLength: 1024 })),
  value: Type.Optional(Type.String({ maxLength: 8192 })),
  expression: Type.Optional(Type.String({ minLength: 1, maxLength: 32000, description: "JavaScript in the page's main frame. Requires interact permission. Return a JSON value; functions, remote objects and BigInt are unsupported." })),
  awaitPromise: Type.Optional(Type.Boolean({ description: "Await a returned Promise (default true)." })),
  returnByValue: Type.Optional(Type.Literal(true)),
  key: Type.Optional(Type.String({ maxLength: 32, description: "Letter/digit, Enter, Tab, Escape, Backspace, Delete, ArrowLeft/Up/Right/Down, Home, End, PageUp/Down, Space." })),
  modifiers: Type.Optional(Type.Integer({ minimum: 0, maximum: 15, description: "Keyboard bitmask: Alt=1, Control=2, Meta=4, Shift=8." })),
  deltaX: Type.Optional(Type.Integer({ minimum: -10000, maximum: 10000 })),
  deltaY: Type.Optional(Type.Integer({ minimum: -10000, maximum: 10000 })),
  condition: Type.Optional(Type.Union([Type.Literal("visible"), Type.Literal("hidden"), Type.Literal("text"), Type.Literal("url"), Type.Literal("loaded")])),
  timeoutMs: Type.Optional(Type.Integer({ minimum: 100, maximum: 20000 })),
});

type BrowserParameters = {
  readonly action: "evaluate" | "open" | "resize" | "set_appearance" | "status" | "navigate" | "snapshot" | "click" | "type" | "screenshot" | "press_key" | "scroll" | "wait";
  readonly url?: string;
  readonly selector?: string;
  readonly value?: string;
};
type Permission = "inspect" | "interact";

const delay = (milliseconds: number, signal: AbortSignal) =>
  new Promise<void>((resolve, reject) => {
    signal.throwIfAborted();
    const abort = () => { clearTimeout(timeout); reject(signal.reason ?? new Error("Browser automation cancelled")); };
    const timeout = setTimeout(() => { signal.removeEventListener("abort", abort); resolve(); }, milliseconds);
    signal.addEventListener("abort", abort, { once: true });
  });

export default function piStationBrowserExtension(pi: ExtensionAPI) {
  const root = process.env.PISTATION_BROWSER_AUTOMATION_ROOT;
  const threadId = process.env.PISTATION_BROWSER_THREAD_ID;

  const request = async (params: BrowserParameters, signal: AbortSignal) => {
    if (!root || !threadId || threadId === "." || threadId === ".." || !/^[A-Za-z0-9._-]{1,160}$/.test(threadId)) {
      throw new Error("Pi Station browser automation is unavailable for this session.");
    }

    const directory = join(root, threadId);
    let permission: Permission;
    let controllerId: string | undefined;
    try {
      const parsed = JSON.parse(await readFile(join(directory, "permission.json"), "utf8")) as {
        mode?: unknown;
        expiresUtc?: unknown;
        controllerId?: unknown;
      };
      if (parsed.mode !== "inspect" && parsed.mode !== "interact") {
        throw new Error("invalid permission");
      }
      if (parsed.expiresUtc !== undefined && (typeof parsed.expiresUtc !== "string" ||
          !Number.isFinite(Date.parse(parsed.expiresUtc)) || Date.parse(parsed.expiresUtc) <= Date.now())) {
        throw new Error("browser controller disconnected");
      }
      permission = parsed.mode;
      controllerId = typeof parsed.controllerId === "string" ? parsed.controllerId : undefined;
    } catch {
      throw new Error("Browser automation is off. Ask the user to enable it in Preview.");
    }

    if (["evaluate", "open", "resize", "set_appearance", "navigate", "click", "type", "press_key", "scroll"].includes(params.action) && permission !== "interact") {
      throw new Error("This browser permission is inspect-only. Ask the user to enable interaction.");
    }

    const id = randomUUID();
    const requestDirectory = join(directory, "requests");
    const responseDirectory = join(directory, "responses");
    await mkdir(requestDirectory, { recursive: true });
    await mkdir(responseDirectory, { recursive: true });
    const requestPath = join(requestDirectory, `${id}.json`);
    const temporaryPath = `${requestPath}.tmp`;
    const responsePath = join(responseDirectory, `${id}.json`);
    const envelope = JSON.stringify({ id, operation: params.action, input: params, createdUtc: new Date().toISOString(), controllerId });
    if (Buffer.byteLength(envelope, "utf8") > 64 * 1024) throw new Error("The browser request exceeds 64 KiB; shorten its input.");
    await writeFile(temporaryPath, envelope, "utf8");
    await rename(temporaryPath, requestPath);

    try {
      const deadline = Date.now() + 30_000;
      while (Date.now() < deadline) {
        signal.throwIfAborted();
        try {
          const response = JSON.parse(await readFile(responsePath, "utf8")) as {
            success?: boolean;
            data?: unknown;
            error?: string;
            screenshotPng?: string;
          };
          await unlink(responsePath).catch(() => undefined);
          if (!response.success) throw new Error(response.error || "Browser operation failed.");
          return response;
        } catch (cause) {
          const code = (cause as { code?: string }).code;
          if (code !== "ENOENT") throw cause;
        }
        await delay(100, signal);
      }
      throw new Error("The Pi Station preview did not answer the browser request within 30 seconds.");
    } finally {
      await unlink(requestPath).catch(() => undefined);
    }
  };

  pi.registerTool({
    name: "pistation_browser",
    label: "Pi Station Browser",
    description:
      "Inspect or interact with permissioned Pi Station Preview tabs in this agent's thread, including while the human views another thread. status lists stable tab IDs. open creates/reuses a tab with optional url; reuseExistingTab=false creates another; open=false keeps it in the background. Omitted tabId uses this agent thread's pinned tab, independent of human tab selection. resize accepts mode fill, freeform with width/height (240..3840, max 8294400 pixels), or preset desktop/tablet/phone with optional portrait/landscape orientation. It confirms rendered CSS dimensions without changing the user agent. set_appearance accepts colorScheme system/light/dark. wait supports visible/hidden selectors, text containing value, URL containing value, or document loaded; timeoutMs defaults to 5000 (max 20000). Closing tabs, disconnecting or revoking permission cancels work; switching the viewed thread does not.",
    promptSnippet: "Open or target a permissioned browser tab; inspect rich snapshots, evaluate JavaScript, resize, navigate, interact or capture it",
    promptGuidelines: [
      "Use pistation_browser only for browser work the user requested; respect inspect-only permission and never ask to broaden it unnecessarily.",
      "Prefer snapshot and semantic actions. snapshot includes page text, element selectors/bounds, an accessibility tree, console/network failures, action history and a PNG image. Check truncated flags; diagnostics begin when automation first attaches and are cleared when access is disabled.",
      "evaluate requires expression and interact permission, returns {type,value}, awaits promises by default, and limits results to 64000 UTF-8 bytes. timeoutMs defaults to 5000 (max 20000). Cancellation attempts to stop execution; an unresponsive browser is closed. Already applied page changes and separately scheduled work are not rolled back.",
    ],
    parameters: PARAMETERS,
    async execute(_toolCallId, params, signal) {
      try {
        const response = await request(params, signal);
        const data = response.data;
        const content: Array<{ type: "text"; text: string } | { type: "image"; data: string; mimeType: string }> =
          [{ type: "text", text: JSON.stringify(data ?? {}, null, 2) }];
        if ((params.action === "screenshot" || params.action === "snapshot") && response.screenshotPng) {
          const png = Buffer.from(response.screenshotPng, "base64");
          if (png.length > 4 * 1024 * 1024 || png.length < 8 || !png.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10])))
            throw new Error("The browser returned an invalid or oversized screenshot.");
          content.push({ type: "image", data: response.screenshotPng, mimeType: "image/png" });
        }
        return {
          content,
          details: { data },
        };
      } catch (cause) {
        const message = cause instanceof Error ? cause.message : String(cause);
        return {
          content: [{ type: "text", text: message }],
          details: { error: message },
          isError: true,
        };
      }
    },
  });
}
