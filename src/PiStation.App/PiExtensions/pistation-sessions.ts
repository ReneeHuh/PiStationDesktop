import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";

const markerType = "pistation.branch-navigation";
const textContent = (content: any) => typeof content === "string" ? content
  : (content ?? []).filter((part: any) => part.type === "text").map((part: any) => part.text).join("\n");

// Use Pi's command-context API so extension hooks, summaries and cancellation retain Pi semantics.
export default function registerSessions(pi: any) {
  pi.registerCommand("pistation-desktop-sessions", {
    description: "PiStation session navigation and labels",
    handler: async (payload: string, ctx: any) => {
      let request: any;
      try {
        if (payload.length > 64 * 1024) throw new Error("Session navigation request is too large.");
        request = JSON.parse(Buffer.from(payload, "base64url").toString("utf8"));
        const labeling = request.action === "label";
        const receiptType = labeling ? "pistation.session-label" : markerType;
        if (!/^[a-f0-9]{32}$/.test(request.id ?? "") || !/^[a-f0-9]{32}$/.test(request.operationId ?? "") ||
            !/^[A-F0-9]{64}$/.test(request.requestHash ?? "") || typeof request.entryId !== "string" ||
            !request.entryId.length || request.entryId.length > 256 ||
            (request.action !== undefined && request.action !== "label") ||
            (!labeling && typeof request.summarize !== "boolean") ||
            (labeling && request.label != null && (typeof request.label !== "string" || request.label.length > 256 || /[\x00-\x1f\x7f-\x9f]/.test(request.label))) ||
            (request.customInstructions?.length ?? 0) > 16384) throw new Error("Invalid session navigation request.");
        if (!ctx.isIdle() || ctx.hasPendingMessages()) throw new Error("Finish the active turn and queued prompts before switching branches.");
        const manager = ctx.sessionManager;
        const entries = manager.getEntries();
        const previous = entries.find((entry: any) => entry.type === "custom" && entry.customType === receiptType &&
          entry.data?.operationId === request.operationId);
        if (previous) {
          if (previous.data.requestHash !== request.requestHash) throw new Error("This navigation identity was already used for a different selection.");
          ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data: previous.data.result }));
          return;
        }
        const path = manager.getSessionFile();
        if (!path || manager.getSessionId() !== request.sessionId) throw new Error("The selected session changed. Refresh its tree.");
        const revision = createHash("sha256").update(readFileSync(path)).digest("hex").toUpperCase();
        if (revision !== request.expectedRevision) throw new Error("The session changed. Refresh its tree before switching branches.");
        const target = manager.getEntry(request.entryId);
        if (!target) throw new Error("The selected entry no longer exists.");
        if (labeling) {
          const label = request.label?.trim() || undefined;
          pi.setLabel(request.entryId, label);
          const result = { label: label ?? null };
          pi.appendEntry(receiptType, { operationId: request.operationId, requestHash: request.requestHash, targetId: request.entryId, result });
          ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data: result }));
          return;
        }
        const oldLeaf = manager.getLeafId();
        const editorText = request.entryId === oldLeaf ? undefined : target.type === "custom_message" ? textContent(target.content)
          : target.type === "message" && target.message.role === "user" ? textContent(target.message.content) : undefined;
        if ((editorText?.length ?? 0) > 128 * 1024) throw new Error("The selected prompt exceeds PiStation's 128K character limit. Choose another conversation point.");
        const navigation = await ctx.navigateTree(request.entryId, {
          summarize: request.summarize, customInstructions: request.customInstructions ?? undefined,
          replaceInstructions: request.replaceInstructions === true,
        });
        const result = { cancelled: navigation.cancelled, editorText: navigation.cancelled ? undefined : editorText };
        // branch()/resetLeaf() only move an in-memory pointer. An SDK custom entry persists
        // the chosen ancestry, including an empty root, without adding an agent message.
        if (!navigation.cancelled) pi.appendEntry(markerType, {
          operationId: request.operationId, requestHash: request.requestHash, targetId: request.entryId, result,
        });
        ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data: result }));
      } catch (error) {
        if (request?.id) ctx.ui.setStatus("pistation-management:" + request.id,
          JSON.stringify({ success: false, error: String((error as Error).message).slice(0, 4096) }));
      }
    },
  });
}
