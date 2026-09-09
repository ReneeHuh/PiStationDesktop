// Native subagents use isolated Pi SDK sessions in the owned runtime.
import { createAgentSession, DefaultResourceLoader, getAgentDir, ModelRuntime, SessionManager, SettingsManager } from "@earendil-works/pi-coding-agent";
import * as PiSdk from "@earendil-works/pi-coding-agent";
import { Type } from "@sinclair/typebox";
import { createHash, randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { filterChildTools, reviewToolCall } from "./pistation-permissions.ts";

const command = "pistation-desktop-agents";
const toolName = "pistation_subagent";
const builtins = ["read", "grep", "find", "ls", "bash", "edit", "write",
  ...(process.platform === "win32" && typeof PiSdk.createPowerShellTool === "function" ? ["powershell"] : [])];
type Preset = { name: string; description: string; systemPrompt: string; tools: string[]; model?: string | null };
const defaults: Preset[] = [
  { name: "scout", description: "Find relevant code and summarize evidence", systemPrompt: "Investigate the task using read/search tools. Return concise findings with file references. Do not make changes.", tools: builtins.slice(0, 4) },
  { name: "planner", description: "Develop an implementation plan", systemPrompt: "Inspect the code and propose a concrete implementation plan with validation steps. Do not make changes.", tools: builtins.slice(0, 4) },
  { name: "reviewer", description: "Review changes and report actionable defects", systemPrompt: "Review the requested code. Report concrete defects, supporting evidence and validation gaps. Do not make changes.", tools: builtins.slice(0, 4) },
  { name: "worker", description: "Implement and verify a delegated task", systemPrompt: "Complete the delegated task and run relevant checks. Describe your changes and results. Other agents may share this workspace; preserve their changes.", tools: builtins },
];
const clip = (value: unknown, count = 32768) => String(value ?? "").slice(0, count);
const identity = () => randomUUID().replaceAll("-", "");
function atomic(path: string, value: unknown) {
  mkdirSync(dirname(path), { recursive: true });
  const temporary = path + "." + identity() + ".tmp";
  try { writeFileSync(temporary, JSON.stringify(value), { encoding: "utf8", mode: 0o600 }); renameSync(temporary, path); }
  finally { if (existsSync(temporary)) unlinkSync(temporary); }
}
function read(path: string, limit = 256 * 1024) {
  if (statSync(path).size > limit) throw new Error("Saved agent data exceeds its size limit.");
  return JSON.parse(readFileSync(path, "utf8"));
}
function validatePreset(value: any): Preset {
  if (!value || !/^[a-z][a-z0-9-]{0,39}$/.test(value.name) || typeof value.description !== "string" || value.description.length > 240 ||
      typeof value.systemPrompt !== "string" || !value.systemPrompt.trim() || value.systemPrompt.length > 8192 ||
      !Array.isArray(value.tools) || value.tools.length < 1 || value.tools.length > builtins.length || value.tools.some((tool: string) => !builtins.includes(tool)) ||
      value.model != null && (typeof value.model !== "string" || value.model.length > 240 || !value.model.includes("/")))
    throw new Error("Use a unique lowercase agent name, instructions, supported tools and an optional provider/model identifier.");
  return { name: value.name, description: value.description, systemPrompt: value.systemPrompt, tools: [...new Set<string>(value.tools)], model: value.model || null };
}
const textOf = (message: any) => typeof message.content === "string" ? message.content : (message.content ?? []).map((block: any) =>
  block.type === "text" ? block.text : block.type === "toolCall" ? `[${block.name}] ${clip(JSON.stringify(block.arguments), 2000)}` : "").filter(Boolean).join("\n");

export default function (pi: any) {
  const root = process.env.PISTATION_AGENT_ROOT;
  const settingsPath = process.env.PISTATION_AGENT_SETTINGS;
  const active = new Map<string, AbortController>();
  const reserved = new Set<string>();
  let prepared: any;
  let includeAssignment = false;
  const settings = () => {
    const value = settingsPath && existsSync(settingsPath) ? read(settingsPath) : { enabled: true, presets: defaults };
    if (typeof value.enabled !== "boolean" || !Array.isArray(value.presets) || value.presets.length > 16) throw new Error("Invalid agent settings. Restore the settings file before using workflows.");
    const presets = value.presets.map(validatePreset);
    if (new Set(presets.map((p: Preset) => p.name)).size !== presets.length) throw new Error("Agent names must be unique.");
    return { enabled: value.enabled, presets, revision: createHash("sha256").update(JSON.stringify(value)).digest("hex") };
  };
  const registered = () => pi.getAllTools().some((tool: any) => tool.name === toolName);
  const snapshot = (ctx: any) => ({ sessionId: ctx.sessionManager.getSessionId(), available: !!root && !!settingsPath && registered(),
    ...settings(), message: !registered()
      ? "The pistation_subagent tool is not registered in this runtime. Review the host allowlist/exclusions and restart before delegating. Presets can still be edited."
      : "Bundled Pi SDK integration. Children use their preset's built-in tools intersected with dedicated host tool restrictions and share the workspace. Model and thinking settings inherit from the parent unless the preset specifies a model." });
  pi.registerCommand(command, { description: "PiStation agent setup and child controls", handler: async (payload: string, ctx: any) => {
    let request: any;
    try {
      if (payload.length > 768 * 1024) throw new Error("Agent request is too large.");
      request = JSON.parse(Buffer.from(payload, "base64url").toString("utf8"));
      if (!/^[a-f0-9]{32}$/.test(request.id)) throw new Error("Invalid request identity.");
      if (!root || !settingsPath) throw new Error("PiStation agent storage is unavailable. Restart the runtime.");
      if (request.action === "stop") {
        const child = active.get(request.controlId);
        if (!child) throw new Error("This child is no longer running.");
        child.abort();
      } else if (request.action !== "inspect") {
        if (!ctx.isIdle() || ctx.hasPendingMessages() || active.size) throw new Error("Finish the active workflow before changing agent setup.");
        const current = settings();
        if (request.expectedRevision !== current.revision) throw new Error("Agent settings changed. Refresh before saving.");
        if (request.action === "prepare") {
          if (!current.enabled) throw new Error("Agent workflows are disabled.");
          if (!pi.getActiveTools().includes(toolName)) throw new Error("The pistation_subagent tool is not active. Review tool selection and planning state before delegating.");
          prepared = request.workflow;
        } else if (request.action === "enable") current.enabled = true;
        else if (request.action === "disable") current.enabled = false;
        else if (request.action === "save") {
          const preset = validatePreset(request.preset);
          current.presets = [...current.presets.filter((p: Preset) => p.name !== preset.name), preset];
          if (current.presets.length > 16) throw new Error("At most 16 agent presets are supported.");
        } else if (request.action === "delete") {
          current.presets = current.presets.filter((p: Preset) => p.name !== request.preset?.name);
        } else throw new Error("Unknown agent action.");
        const saved = { enabled: current.enabled, presets: current.presets };
        if (Buffer.byteLength(JSON.stringify(saved), "utf8") > 256 * 1024) throw new Error("Agent settings exceed the 256 KiB limit. Shorten the preset instructions.");
        if (request.action !== "prepare") atomic(settingsPath, saved);
      }
      ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data: snapshot(ctx) }));
    } catch (error) {
      if (request?.id) ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: false, error: (error as Error).message }));
    }
  } });
  pi.on("session_shutdown", () => { for (const controller of active.values()) controller.abort(); });
  pi.on("before_agent_start", () => {
    const workflow = prepared; prepared = undefined; includeAssignment = !!workflow;
    if (workflow) return { message: { customType: "pistation-agent-workflow", display: false,
      content: "Delegate this exact workflow with pistation_subagent, then summarize the results.\nPISTATION_AGENT_WORKFLOW\n" + JSON.stringify(workflow) } };
  });
  pi.on("context", (event: any) => {
    const latest = event.messages.findLastIndex((message: any) => message.customType === "pistation-agent-workflow");
    return { messages: event.messages.filter((message: any, index: number) => message.customType !== "pistation-agent-workflow" || includeAssignment && index === latest) };
  });

  const taskSchema = Type.Object({ agent: Type.String(), task: Type.String() });
  pi.registerTool({ name: toolName, label: "PiStation agents",
    description: "Run isolated child sessions using PiStation presets. Modes: single, parallel (up to 8 tasks, 4 concurrent), chain (sequential, {previous} inserts the last result). Use tasks: [{agent, task}]. Presets default to scout, planner, reviewer, worker; native Agents setup lists custom presets. Resume a saved child with resumeId in single mode. Children share workspace files; avoid overlapping writes. Planning policy requires approval before delegation.",
    parameters: Type.Object({ mode: Type.Union([Type.Literal("single"), Type.Literal("parallel"), Type.Literal("chain")]), tasks: Type.Array(taskSchema, { minItems: 1, maxItems: 8 }), resumeId: Type.Optional(Type.String()) }),
    async execute(_callId: string, params: any, signal: AbortSignal, onUpdate: any, ctx: any) {
      if (!root || !settingsPath) throw new Error("PiStation agent storage is unavailable.");
      const config = settings();
      if (!config.enabled) throw new Error("Agent workflows are disabled. Enable them in the Agents panel.");
      if (!["single", "parallel", "chain"].includes(params.mode) || !Array.isArray(params.tasks) || !params.tasks.length || params.tasks.length > 8 ||
          params.mode === "single" && params.tasks.length !== 1 || params.resumeId && (params.mode !== "single" || !/^[a-f0-9]{32}$/.test(params.resumeId)))
        throw new Error("Invalid workflow. Use one single task or up to eight parallel/chain tasks.");
      const tasks = params.tasks.map((task: any) => {
        if (typeof task.task !== "string" || !task.task.trim() || task.task.length > 8192) throw new Error("Each task needs 1–8192 characters.");
        const preset = params.resumeId ? validatePreset(read(join(root, params.resumeId, "child.json")).preset)
          : config.presets.find((p: Preset) => p.name === task.agent);
        if (!preset) throw new Error("Unknown agent preset: " + clip(task.agent, 80));
        return { task: task.task, preset };
      });
      if (params.resumeId && reserved.has(params.resumeId)) throw new Error("That child already has an active continuation.");
      const results: any[] = tasks.map((task: any, index: number) => ({ agent: task.preset.name, task: task.task, controlId: identity(),
        status: "pending", exitCode: -1, messages: [], transcript: "", usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, cost: 0 }, step: params.mode === "chain" ? index + 1 : undefined }));
      if (params.resumeId) reserved.add(params.resumeId);
      let lastUpdate = 0;
      let updateTimer: ReturnType<typeof setTimeout> | undefined;
      const emit = (force = false) => {
        if (!force && Date.now() - lastUpdate < 200) {
          updateTimer ??= setTimeout(() => { updateTimer = undefined; emit(true); }, 200);
          return;
        }
        if (updateTimer) { clearTimeout(updateTimer); updateTimer = undefined; }
        lastUpdate = Date.now();
        onUpdate?.({ content: [{ type: "text", text: results.map(r => `${r.agent}: ${r.status}`).join("\n") }], details: { integration: "pistation", mode: params.mode, results } });
      };
      const stopAll = () => { for (const result of results) active.get(result.controlId)?.abort(); };
      signal?.addEventListener("abort", stopAll, { once: true });
      async function run(index: number, previous = "") {
        const result = results[index];
        const controller = new AbortController();
        active.set(result.controlId, controller);
        if (signal?.aborted) controller.abort();
        let session: any;
        let unsubscribe: (() => void) | undefined;
        let timedOut = false;
        const timer = setTimeout(() => { timedOut = true; controller.abort(); }, 30 * 60 * 1000);
        const abort = () => { if (session) void session.abort().catch(() => {}); }; // The awaited prompt reports the terminal outcome.
        controller.signal.addEventListener("abort", abort, { once: true });
        result.status = "running"; emit(true);
        try {
          if (controller.signal.aborted) throw new Error("Child interrupted before it started.");
          let preset = tasks[index].preset;
          let manager: any;
          if (params.resumeId) {
            const saved = read(join(root!, params.resumeId, "child.json"));
            preset = validatePreset(saved.preset);
            if (!/^[a-zA-Z0-9_.-]+\.jsonl$/.test(saved.sessionFile)) throw new Error("Saved child session is invalid.");
            const oldFile = join(root!, params.resumeId, saved.sessionFile);
            if (!existsSync(oldFile) || statSync(oldFile).size > 16 * 1024 * 1024) throw new Error("The saved child session is unavailable or too large.");
            // Continue a copy so earlier activity and transcript snapshots stay immutable.
            const childDir = join(root!, result.controlId);
            mkdirSync(childDir, { recursive: true });
            const newFile = join(childDir, saved.sessionFile);
            writeFileSync(newFile, readFileSync(oldFile));
            manager = SessionManager.open(newFile, childDir, ctx.cwd);
          } else manager = SessionManager.create(ctx.cwd, join(root!, result.controlId));
          const runtime = await ModelRuntime.create();
          for (const id of ctx.modelRegistry.getRegisteredProviderIds()) {
            const native = ctx.modelRegistry.getRegisteredNativeProvider(id);
            const provider = ctx.modelRegistry.getRegisteredProviderConfig(id);
            if (native) runtime.registerNativeProvider(native); else if (provider) runtime.registerProvider(id, provider);
          }
          const modelName = preset.model || (ctx.model ? `${ctx.model.provider}/${ctx.model.id}` : "");
          const slash = modelName.indexOf("/");
          const model = slash > 0 ? runtime.getModel(modelName.slice(0, slash), modelName.slice(slash + 1)) : undefined;
          if (!model) throw new Error("The selected child model is unavailable: " + modelName);
          const memorySettings = SettingsManager.inMemory({ retry: { enabled: false }, compaction: { enabled: false }, packages: [] });
          const loader = new DefaultResourceLoader({ cwd: ctx.cwd, agentDir: getAgentDir(), settingsManager: memorySettings,
            extensionFactories: [{ name: "PiStation child permissions", factory: (child: any) => {
              child.on("tool_call", (event: any) => reviewToolCall(event, ctx, preset.name));
            } }],
            noExtensions: true, noSkills: true, noPromptTemplates: true, noThemes: true,
            appendSystemPrompt: [preset.systemPrompt] });
          await loader.reload();
          ({ session } = await createAgentSession({ cwd: ctx.cwd, modelRuntime: runtime, model,
            thinkingLevel: ctx.thinkingLevel, tools: filterChildTools(preset.tools), sessionManager: manager, settingsManager: memorySettings, resourceLoader: loader }));
          const sessionFile = manager.getSessionFile();
          if (!sessionFile) throw new Error("The child session has no persistence path.");
          atomic(join(root!, result.controlId, "child.json"), { preset, sessionFile: sessionFile.replaceAll("\\", "/").split("/").at(-1) });
          result.model = modelName; result.thinkingLevel = session.thinkingLevel;
          result.task = clip(tasks[index].task.replaceAll("{previous}", previous), 32768);
          const capture = (message: any, newMessage = true) => {
            result.canResume = existsSync(sessionFile);
            const fullText = textOf(message);
            const content = fullText.length > 8192 ? fullText.slice(0, 8100) + "\n[Message truncated; full text is in the saved child session.]" : fullText;
            if (content) {
              const transcript = result.transcript + `\n\n${message.role}${message.toolName ? " · " + message.toolName : ""}:\n${content}`;
              result.transcript = transcript.length > 32768 ? "[Earlier transcript omitted]\n" + transcript.slice(-32730) : transcript;
            }
            result.messages.push({ role: message.role, content: [{ type: "text", text: content }] });
            result.messages = result.messages.slice(-8);
            if (newMessage && message.role === "assistant") {
              result.stopReason = message.stopReason;
              result.errorMessage = clip(message.errorMessage, 4000);
              for (const key of ["input", "output", "cacheRead", "cacheWrite"]) result.usage[key] += message.usage?.[key] ?? 0;
              result.usage.cost += message.usage?.cost?.total ?? 0;
            }
          };
          for (const message of session.messages) capture(message, false);
          unsubscribe = session.subscribe((event: any) => {
            if (event.type === "message_end") capture(event.message);
            if (event.type === "tool_execution_start") { result.toolCount = (result.toolCount ?? 0) + 1; result.currentActivity = "Using " + event.toolName; }
            if (event.type === "message_update" && event.assistantMessageEvent?.type === "text_delta") result.currentActivity = "Writing response";
            emit();
          });
          if (controller.signal.aborted) throw new Error("Child interrupted before dispatch.");
          await session.prompt(result.task);
          if (timedOut) throw new Error("Child exceeded the 30-minute run limit.");
          result.exitCode = result.stopReason === "error" ? 1 : 0;
          if (controller.signal.aborted || result.stopReason === "aborted") result.stopReason = "aborted";
          result.status = result.stopReason === "aborted" ? "interrupted" : result.exitCode ? "failed" : "completed";
          if (sessionFile && existsSync(sessionFile)) {
            result.canResume = true;
          }
        } catch (error) {
          result.exitCode = 1;
          result.stopReason = controller.signal.aborted && !timedOut ? "aborted" : "error";
          result.status = result.stopReason === "aborted" ? "interrupted" : "failed";
          result.errorMessage = timedOut ? "Child exceeded the 30-minute run limit." : clip((error as Error).message, 4000);
        } finally {
          unsubscribe?.(); session?.dispose(); clearTimeout(timer);
          controller.signal.removeEventListener("abort", abort); active.delete(result.controlId); emit(true);
        }
        return [...result.messages].reverse().find((message: any) => message.role === "assistant")?.content[0]?.text ?? result.errorMessage ?? "";
      }
      try {
        emit(true);
        if (params.mode === "parallel") {
          let next = 0;
          await Promise.all(Array.from({ length: Math.min(4, tasks.length) }, async () => { while (next < tasks.length) await run(next++); }));
        } else {
          let previous = "";
          for (let index = 0; index < tasks.length; index++) {
            previous = await run(index, previous);
            if (results[index].status !== "completed") {
              for (const pending of results.slice(index + 1)) Object.assign(pending, { status: "interrupted", stopReason: "aborted", exitCode: 1, errorMessage: "Not started because an earlier chain step did not complete." });
              break;
            }
          }
        }
      } finally { signal?.removeEventListener("abort", stopAll); if (params.resumeId) reserved.delete(params.resumeId); if (updateTimer) clearTimeout(updateTimer); }
      emit(true);
      return { content: [{ type: "text", text: clip(results.map(result => `${result.agent} (${result.status})\n${result.errorMessage || [...result.messages].reverse().find((m: any) => m.role === "assistant")?.content[0]?.text || "No output"}`).join("\n\n")) }],
        details: { integration: "pistation", mode: params.mode, results }, isError: results.some(result => result.status !== "completed") };
    },
  });
}
