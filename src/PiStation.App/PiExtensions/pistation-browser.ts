import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { randomUUID } from "node:crypto";
import { mkdir, readFile, rename, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { Type } from "typebox";

const PARAMETERS = Type.Object({
  action: Type.Union([
    Type.Literal("status"),
    Type.Literal("navigate"),
    Type.Literal("snapshot"),
    Type.Literal("click"),
    Type.Literal("type"),
    Type.Literal("screenshot"),
  ]),
  url: Type.Optional(Type.String({ maxLength: 2048 })),
  selector: Type.Optional(Type.String({ maxLength: 1024 })),
  value: Type.Optional(Type.String({ maxLength: 8192 })),
});

type BrowserParameters = {
  readonly action: "status" | "navigate" | "snapshot" | "click" | "type" | "screenshot";
  readonly url?: string;
  readonly selector?: string;
  readonly value?: string;
};
type Permission = "inspect" | "interact";

const delay = (milliseconds: number, signal: AbortSignal) =>
  new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(resolve, milliseconds);
    signal.addEventListener(
      "abort",
      () => {
        clearTimeout(timeout);
        reject(signal.reason ?? new Error("Browser automation cancelled"));
      },
      { once: true },
    );
  });

export default function piStationBrowserExtension(pi: ExtensionAPI) {
  const root = process.env.PISTATION_BROWSER_AUTOMATION_ROOT;
  const threadId = process.env.PISTATION_BROWSER_THREAD_ID;

  const request = async (params: BrowserParameters, signal: AbortSignal) => {
    if (!root || !threadId || !/^[A-Za-z0-9._-]{1,160}$/.test(threadId)) {
      throw new Error("Pi Station browser automation is unavailable for this session.");
    }

    const directory = join(root, threadId);
    let permission: Permission;
    try {
      const parsed = JSON.parse(await readFile(join(directory, "permission.json"), "utf8")) as {
        mode?: unknown;
      };
      if (parsed.mode !== "inspect" && parsed.mode !== "interact") {
        throw new Error("invalid permission");
      }
      permission = parsed.mode;
    } catch {
      throw new Error("Browser automation is off. Ask the user to enable it in Preview.");
    }

    if (["navigate", "click", "type"].includes(params.action) && permission !== "interact") {
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
    await writeFile(
      temporaryPath,
      JSON.stringify({ id, operation: params.action, input: params, createdUtc: new Date().toISOString() }),
      "utf8",
    );
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
          };
          await unlink(responsePath).catch(() => undefined);
          if (!response.success) throw new Error(response.error || "Browser operation failed.");
          return response.data;
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
      "Inspect or interact with the browser tab visible in Pi Station Preview when the user has granted access.",
    promptSnippet: "Inspect, navigate, click, type, or capture the permissioned Pi Station Preview tab",
    promptGuidelines: [
      "Use pistation_browser only for browser work the user requested; respect inspect-only permission and never ask to broaden it unnecessarily.",
    ],
    parameters: PARAMETERS,
    async execute(_toolCallId, params, signal) {
      try {
        const data = await request(params, signal);
        return {
          content: [{ type: "text", text: JSON.stringify(data, null, 2) }],
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
