// Runs the real Pi agent loop without network requests, credentials, or user resources.
import { appendFileSync } from "node:fs";
import { createAssistantMessageEventStream } from "@earendil-works/pi-ai/compat";
import { Type } from "@sinclair/typebox";

export default function (pi: any) {
  pi.registerProvider("pistation-offline", {
    baseUrl: "http://127.0.0.1:1",
    apiKey: "offline-fixture",
    api: "pistation-offline-api",
    models: [{
      id: "deterministic", name: "PiStation offline fixture", reasoning: false,
      input: ["text", "image"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
      contextWindow: 100000, maxTokens: 1000,
    }],
    streamSimple(model: any, context: any) {
      const stream = createAssistantMessageEventStream();
      appendFileSync(process.env.PISTATION_TEST_TRACE!, JSON.stringify(context) + "\n");
      const last = context.messages.at(-1);
      const callTool = last?.role === "user" && JSON.stringify(last.content).includes("call probe tool");
      const message: any = {
        role: "assistant", api: model.api, provider: model.provider, model: model.id,
        content: callTool
          ? [{ type: "toolCall", id: "probe-call", name: "pistation_probe", arguments: {} }]
          : [{ type: "text", text: "PISTATION_REAL_RPC_OK" }],
        usage: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0, totalTokens: 15,
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
        stopReason: callTool ? "toolUse" : "stop", timestamp: Date.now(),
      };
      queueMicrotask(() => {
        stream.push({ type: "start", partial: message });
        stream.push({ type: "done", reason: message.stopReason, message });
        stream.end();
      });
      return stream;
    },
  });
  pi.registerTool({
    name: "pistation_probe", label: "PiStation probe", description: "An offline integration probe.",
    parameters: Type.Object({}),
    async execute() { return { content: [{ type: "text", text: "PISTATION_TOOL_OK" }], details: {} }; },
  });
  pi.registerCommand("pistation-probe", {
    description: "Exercise extension UI without a model turn.",
    handler: async (_args: string, ctx: any) => {
      ctx.ui.notify("PROBE_NOTIFICATION", "warning");
      ctx.ui.setStatus("probe", "PROBE_STATUS");
      ctx.ui.setWidget("probe-above", ["PROBE_ABOVE"]);
      ctx.ui.setWidget("probe-below", ["PROBE_BELOW"], { placement: "belowEditor" });
      ctx.ui.setTitle("PROBE_TITLE");
      ctx.ui.setEditorText("PROBE_DRAFT");
    },
  });
  pi.registerCommand("pistation-clear", {
    description: "Clear extension state.",
    handler: async (_args: string, ctx: any) => {
      ctx.ui.setStatus("probe", undefined);
      ctx.ui.setWidget("probe-above", undefined);
      ctx.ui.setWidget("probe-below", undefined);
      ctx.ui.setEditorText("");
    },
  });
}
