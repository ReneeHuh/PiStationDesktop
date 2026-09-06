import { createAssistantMessageEventStream } from "@earendil-works/pi-ai/compat";
import { Type } from "@sinclair/typebox";
import { writeFileSync } from "node:fs";

export default function (pi: any) {
  pi.registerCommand("plan-test-reload", { description: "Reload the offline plan fixture", handler: async (_args: string, ctx: any) => { await ctx.reload(); } });
  // A competing extension deliberately re-enables mutation tools. The plan dispatch hook must still block them.
  pi.on("before_agent_start", () => pi.setActiveTools([...pi.getActiveTools(), "write", "bash", "plan_mutation_probe"]));
  pi.registerTool({ name: "plan_mutation_probe", label: "Mutation probe", description: "Offline policy probe", parameters: Type.Object({}),
    async execute() { writeFileSync("extension-mutation.txt", "changed"); return { content: [{ type: "text", text: "mutated" }], details: {} }; } });
  pi.registerProvider("pistation-plan-offline", {
    baseUrl: "http://127.0.0.1:1", apiKey: "offline", api: "pistation-plan-test",
    models: [{ id: "deterministic", name: "Plan fixture", reasoning: false, input: ["text"],
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 100000, maxTokens: 1000 }],
    streamSimple(model: any, context: any, options: any) {
      const stream = createAssistantMessageEventStream();
      const trigger = [...context.messages].reverse().map((message: any) => JSON.stringify(message.content)).find((text: string) => text.includes("PLAN_TEST_")) ?? "";
      const result = context.messages.at(-1)?.role === "toolResult";
      let calls: any[] = [];
      let text = "Policy probe finished.";
      if (trigger.includes("PLAN_TEST_READ")) {
        if (!result) calls = [{ type: "toolCall", id: "read-test", name: "pistation_plan_read", arguments: { path: "README.md" } }];
        text = "Plan:\n1. Implement the change\n2. Verify the change";
      } else if (trigger.includes("PLAN_TEST_MUTATE") && !result) {
        calls = [
          { type: "toolCall", id: "write-test", name: "write", arguments: { path: "blocked-write.txt", content: "changed" } },
          { type: "toolCall", id: "shell-test", name: "bash", arguments: { command: "echo changed > blocked-shell.txt" } },
          { type: "toolCall", id: "extension-test", name: "plan_mutation_probe", arguments: {} },
        ];
      } else if (trigger.includes("PLAN_TEST_EXECUTE")) {
        if (!result) calls = [
          { type: "toolCall", id: "approved-write", name: "write", arguments: { path: "approved-write.txt", content: "approved" } },
          { type: "toolCall", id: "approved-edit", name: "edit", arguments: { path: "README.md", oldText: "Read-only test project", newText: "Approved edit after reload" } },
        ];
        text = "Implemented.\n[DONE:1]";
      } else if (trigger.includes("PLAN_TEST_FINISH")) text = "Verified.\n[DONE:2]";
      const message: any = { role: "assistant", api: model.api, provider: model.provider, model: model.id,
        content: calls.length ? calls : [{ type: "text", text }], stopReason: calls.length ? "toolUse" : "stop", timestamp: Date.now(),
        usage: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0, totalTokens: 15, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } };
      if (trigger.includes("PLAN_TEST_WAIT")) {
        const abort = () => { message.stopReason = "aborted"; message.content = []; stream.push({ type: "error", reason: "aborted", error: message }); stream.end(); };
        if (options?.signal?.aborted) abort(); else options?.signal?.addEventListener("abort", abort, { once: true });
      } else queueMicrotask(() => { stream.push({ type: "start", partial: message }); stream.push({ type: "done", reason: message.stopReason, message }); stream.end(); });
      return stream;
    },
  });
}
