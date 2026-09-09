// Deterministic provider: real Pi dispatches these tool calls, with no network/model bill.
import { createAssistantMessageEventStream } from "@earendil-works/pi-ai/compat";
import { Type } from "@sinclair/typebox";
import { writeFileSync } from "node:fs";

export default function (pi: any) {
  pi.registerCommand("tools-test-reload", { description: "Reload tool-policy fixture", handler: async (_args: string, ctx: any) => { await ctx.reload(); } });
  pi.registerTool({ name: "tool_policy_probe", label: "Tool policy probe", description: "Excluded extension tool probe",
    parameters: Type.Object({}), async execute() {
      writeFileSync("blocked-extension.txt", "not allowed");
      return { content: [{ type: "text", text: "unexpected mutation" }], details: {} };
    } });
  // A competing extension cannot add tools that the allowlist/exclusion removed.
  // It can reselect registered tools, so planning/approval hooks must still run.
  pi.on("before_agent_start", () => pi.setActiveTools([...pi.getActiveTools(), "write", "powershell", "tool_policy_probe"]));
  pi.registerProvider("pistation-tools-offline", {
    baseUrl: "http://127.0.0.1:1", apiKey: "offline", api: "pistation-tools-test",
    models: [{ id: "deterministic", name: "Tool policy fixture", reasoning: false, input: ["text"],
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 100000, maxTokens: 1000 }],
    streamSimple(model: any, context: any) {
      const stream = createAssistantMessageEventStream();
      const prompt = [...context.messages].reverse().filter((message: any) => message.role === "user")
        .map((message: any) => JSON.stringify(message.content)).find((text: string) => text.includes("TOOLS_")) ?? "";
      const result = context.messages.at(-1)?.role === "toolResult";
      let calls: any[] = [];
      const call = (name: string, args: any) => ({ type: "toolCall", id: name + "-" + Date.now(), name, arguments: args });
      if (!result) {
        if (prompt.includes("TOOLS_BLOCKED")) calls = [
          call("write", { path: "blocked-write.txt", content: "not allowed" }), call("tool_policy_probe", {})];
        else if (prompt.includes("TOOLS_READ")) calls = [call("pistation_plan_read", { path: "README.md" })];
        else if (prompt.includes("TOOLS_WAIT")) calls = [call("powershell", { command: "Write-Output 'TOOL_WAIT_STARTED'; Start-Sleep -Seconds 60", timeout: 90 })];
        else if (prompt.includes("TOOLS_FAIL")) calls = [call("powershell", { command: "Write-Output 'TOOL_EXPECTED_FAILURE'; exit 7" })];
        else if (prompt.includes("TOOLS_PLAN_BLOCK")) calls = [call("powershell", { command: "Set-Content -LiteralPath 'blocked-powershell.txt' -Value 'not allowed'" })];
        else calls = [call("powershell", { command: "Write-Output 'PISTATION_POWERSHELL_OK'; Write-Output 'Unicode café 日本語'" })];
      }
      const message: any = { role: "assistant", api: model.api, provider: model.provider, model: model.id,
        content: calls.length ? calls : [{ type: "text", text: "Tool probe complete." }],
        stopReason: calls.length ? "toolUse" : "stop", timestamp: Date.now(),
        usage: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0, totalTokens: 15,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } };
      queueMicrotask(() => { stream.push({ type: "start", partial: message }); stream.push({ type: "done", reason: message.stopReason, message }); stream.end(); });
      return stream;
    },
  });
}
