// PiStation's tool policy is enforced at dispatch. Trusted Node extensions are not sandboxed.
import { createReadTool, createGrepTool, createFindTool, createLsTool } from "@earendil-works/pi-coding-agent";
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { randomUUID } from "node:crypto";

const command = "pistation-desktop-plan";
const customType = "pistation-plan-v1";
const stateKey = "pistation-plan-state";
const modes = ["off", "planning", "ready", "executing", "paused", "completed"];
const factories = { read: createReadTool, grep: createGrepTool, find: createFindTool, ls: createLsTool };
const readers = Object.keys(factories).map(name => "pistation_plan_" + name);
type Plan = { sessionId: string; revision: number; mode: string; text: string;
  steps: { number: number; text: string; completed: boolean }[]; updatedUtc: string };
const cleanText = (value: string) => value.replace(/```[^\n]*\n[\s\S]*?```/g, "");
function steps(text: string) {
  const lines = [...cleanText(text).matchAll(/^\s*\d+[.)]\s+(.+)$/gm)];
  if (lines.length > 100) throw new Error("A plan can contain at most 100 steps.");
  return lines.map((line, index) => ({ number: index + 1, text: line[1].trim().slice(0, 2000), completed: false }));
}
function validate(value: any, sessionId: string): Plan {
  if (!value || !modes.includes(value.mode) || !Number.isSafeInteger(value.revision) || value.revision < 0 ||
      typeof value.text !== "string" || value.text.length > 32 * 1024 || !Array.isArray(value.steps) || value.steps.length > 100 ||
      value.steps.some((step: any, index: number) => step.number !== index + 1 || typeof step.text !== "string" || step.text.length > 2000 || typeof step.completed !== "boolean"))
    throw new Error("Saved plan is invalid. Restore its state file before using this runtime.");
  return { ...value, sessionId };
}

export default function (pi: any) {
  let state: Plan;
  let failure: string | undefined;
  let normalTools: string[] = [];
  let executionPermit = false;
  const statePath = process.env.PISTATION_PLAN_STATE_PATH;
  const restricted = () => !!failure || !state || !["off", "executing"].includes(state.mode);
  const policy = () => pi.setActiveTools(restricted() ? readers : normalTools);
  const emit = (ctx: any) => ctx.ui.setStatus(stateKey, JSON.stringify(state));
  function persist(ctx: any) {
    state = { ...state, revision: state.revision + 1, updatedUtc: new Date().toISOString() };
    try {
      if (statePath) {
        mkdirSync(dirname(statePath), { recursive: true });
        const temporary = statePath + "." + randomUUID() + ".tmp";
        try { writeFileSync(temporary, JSON.stringify(state), { encoding: "utf8", mode: 0o600 }); renameSync(temporary, statePath); }
        finally { if (existsSync(temporary)) unlinkSync(temporary); }
      }
      pi.appendEntry(customType, state);
    } catch (error) {
      failure = "Plan persistence failed: " + (error as Error).message;
      state.mode = "paused";
      policy();
      throw new Error(failure);
    }
    policy();
    emit(ctx);
  }
  for (const [name, factory] of Object.entries(factories)) {
    const template = factory(process.cwd());
    pi.registerTool({ name: "pistation_plan_" + name, label: "Plan: " + name,
      description: template.description, parameters: template.parameters,
      async execute(id: string, args: any, signal: AbortSignal, update: any, ctx: any) {
        if (failure) throw new Error(failure);
        return factory(ctx.cwd).execute(id, args, signal, update);
      } });
  }
  pi.on("tool_call", (event: any) => {
    if (restricted() && !readers.includes(event.toolName))
      return { block: true, reason: "PiStation planning policy blocks this tool. Only plan read/search tools are available until the user approves execution." };
  });
  pi.on("user_bash", () => restricted() ? { result: { output: "Shell execution is blocked by the PiStation planning policy.", exitCode: 1, cancelled: false, truncated: false } } : undefined);
  pi.on("session_shutdown", (event: any) => {
    // Pi carries the active selection into reload. Hand back the normal selection
    // before the replacement extension captures it and re-applies the saved policy.
    if (event.reason === "reload" && state?.mode !== "off" && !failure) pi.setActiveTools(normalTools);
  });
  pi.registerCommand(command, { description: "PiStation plan workflow",
    handler: async (payload: string, ctx: any) => {
      let request: any;
      try {
        if (payload.length > 64 * 1024) throw new Error("Plan request is too large.");
        request = JSON.parse(Buffer.from(payload, "base64url").toString("utf8"));
        if (!/^[a-f0-9]{32}$/.test(request.id)) throw new Error("Invalid request identity.");
        if (failure) throw new Error(failure);
        if (!ctx.isIdle() || ctx.hasPendingMessages()) throw new Error("Finish the turn and clear queued messages before changing the plan.");
        if (request.action !== "inspect") {
          if (request.expectedRevision !== state.revision) throw new Error("The plan changed. Refresh and review it before retrying.");
          if (!["plan", "save", "execute", "off"].includes(request.action)) throw new Error("Unknown plan action.");
          if (state.mode === "off") normalTools = pi.getActiveTools().filter((name: string) => !readers.includes(name));
          if (request.action === "save") {
            if (typeof request.text !== "string" || request.text.length > 32 * 1024) throw new Error("Plan text must be at most 32 KiB.");
            request.text = request.text.replace(/\r\n?/g, "\n");
            const proposed = steps(request.text);
            if (!proposed.length) throw new Error("Use a numbered list with at least one plan step.");
            state = { ...state, text: request.text, steps: proposed, mode: "ready" };
          } else if (request.action === "execute") {
            if (!["ready", "paused"].includes(state.mode) || !state.steps.some(step => !step.completed)) throw new Error("Review a plan with remaining steps before executing.");
            state.mode = "executing";
            executionPermit = true;
          } else if (request.action === "plan") { state.mode = "planning"; executionPermit = false; }
          else { state.mode = "off"; executionPermit = false; }
          persist(ctx);
        }
        ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data: state }));
      } catch (error) {
        if (request?.id) ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: false, error: (error as Error).message }));
      }
    } });
  pi.on("session_start", (event: any, ctx: any) => {
    const sessionId = ctx.sessionManager.getSessionId();
    normalTools = pi.getActiveTools().filter((name: string) => !readers.includes(name));
    try {
      const transition = ["new", "fork", "resume"].includes(event.reason);
      const saved = !transition && statePath && existsSync(statePath) ? (() => {
        if (statSync(statePath).size > 512 * 1024) throw new Error("Saved plan exceeds its size limit.");
        const value = JSON.parse(readFileSync(statePath, "utf8"));
        if (value.sessionId !== sessionId) throw new Error("Saved plan belongs to another session.");
        return value;
      })() : ctx.sessionManager.getBranch().filter((entry: any) => entry.type === "custom" && entry.customType === customType).at(-1)?.data;
      state = saved ? validate(saved, sessionId) : { sessionId, revision: 0, mode: "off", text: "", steps: [], updatedUtc: new Date().toISOString() };
      const wasExecuting = state.mode === "executing";
      if (wasExecuting) state.mode = "paused";
      if (wasExecuting || transition) persist(ctx);
      policy(); emit(ctx);
    } catch (error) { failure = (error as Error).message; policy(); ctx.ui.notify(failure, "error"); }
  });
  pi.on("before_agent_start", (_event: any, ctx: any) => {
    if (failure) throw new Error(failure);
    if (state.mode === "off") return;
    if (state.mode === "executing") {
      if (!executionPermit) { state.mode = "paused"; persist(ctx); }
      executionPermit = false;
    }
    policy();
    const content = state.mode === "executing"
      ? "Execute this user-approved plan. After each completed step, emit a standalone [DONE:n] line with its step number. Report incomplete work honestly.\n" +
        state.steps.filter(step => !step.completed).map(step => `${step.number}. ${step.text}`).join("\n")
      : "PiStation read-only planning is active. Only dedicated plan read/search tools are permitted. Analyze the user's request without making changes. Propose or refine a numbered list under a Plan: heading. Execution requires approval through the native Plan panel.\nCurrent plan:\n" + state.text;
    return { message: { customType: "pistation-plan-context", content, display: false } };
  });
  pi.on("context", (event: any) => {
    const latest = event.messages.findLastIndex((message: any) => message.customType === "pistation-plan-context");
    return { messages: event.messages.filter((message: any, index: number) => message.customType !== "pistation-plan-context" || state?.mode !== "off" && index === latest) };
  });
  pi.on("session_tree", (_event: any, ctx: any) => {
    if (failure) return;
    const saved = ctx.sessionManager.getBranch().filter((entry: any) => entry.type === "custom" && entry.customType === customType).at(-1)?.data;
    const revision = state.revision;
    state = saved ? validate(saved, ctx.sessionManager.getSessionId()) : { ...state, mode: "off", text: "", steps: [] };
    state.revision = Math.max(state.revision, revision);
    if (state.mode === "executing") state.mode = "paused";
    executionPermit = false;
    persist(ctx);
  });
  pi.on("turn_end", (event: any, ctx: any) => {
    if (state?.mode !== "executing" || event.message?.role !== "assistant" || event.message.stopReason !== "stop") return;
    const text = cleanText((event.message.content ?? []).filter((block: any) => block.type === "text").map((block: any) => block.text).join("\n"));
    let changed = false;
    for (const match of text.matchAll(/^\s*\[DONE:(\d+)\]\s*$/gm)) {
      const step = state.steps[Number(match[1]) - 1];
      if (step && !step.completed) { step.completed = true; changed = true; }
    }
    if (changed) persist(ctx);
  });
  pi.on("agent_end", (event: any, ctx: any) => {
    if (!state || failure) return;
    if (state.mode === "executing") {
      state.mode = state.steps.every(step => step.completed) ? "completed" : "paused";
      executionPermit = false; persist(ctx); return;
    }
    if (!["planning", "ready", "paused"].includes(state.mode)) return;
    const last = [...event.messages].reverse().find((message: any) => message.role === "assistant");
    if (last?.stopReason !== "stop") return;
    const text = (last.content ?? []).filter((block: any) => block.type === "text").map((block: any) => block.text).join("\n");
    const match = /^\s*(?:#{1,6}\s*)?(?:\*\*)?Plan:?(?:\*\*)?\s*$/im.exec(text);
    if (match) {
      const proposed = text.slice(match.index).slice(0, 32 * 1024);
      const items = steps(proposed);
      if (items.length) { state = { ...state, text: proposed, steps: items, mode: "ready" }; persist(ctx); }
    }
  });
}
