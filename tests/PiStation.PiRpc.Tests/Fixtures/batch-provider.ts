import { createAssistantMessageEventStream } from "@earendil-works/pi-ai/compat";
import { Type } from "@sinclair/typebox";
import { appendFileSync } from "node:fs";

export default function (pi: any) {
  for (const name of ["parallel_probe", "sequential_probe"]) pi.registerTool({ name, label: name, description: name,
    executionMode: name === "sequential_probe" ? "sequential" : "parallel", parameters: Type.Object({ index: Type.Number() }),
    async execute(_id: string, args: any) {
      appendFileSync(process.env.PISTATION_TEST_TRACE!, `start${args.index}\n`);
      await new Promise(resolve => setTimeout(resolve, 150));
      appendFileSync(process.env.PISTATION_TEST_TRACE!, `end${args.index}\n`);
      return { content: [{ type: "text", text: "done" }], details: {} };
    } });
  pi.registerProvider("pistation-batch-offline", {
    baseUrl: "http://127.0.0.1:1", apiKey: "offline", api: "pistation-batch-test",
    models: [{ id: "deterministic", name: "Batch fixture", reasoning: false, input: ["text"],
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 100000, maxTokens: 1000 }],
    streamSimple(model: any, context: any) {
      const stream = createAssistantMessageEventStream();
      const last = context.messages.at(-1);
      const barrier = JSON.stringify(last?.content).includes("barrier");
      const calls = last?.role === "user" ? [0, 1].map(index => ({ type: "toolCall", id: "probe-" + index + "-" + Date.now(),
        name: barrier && index === 1 ? "sequential_probe" : "parallel_probe", arguments: { index } })) : [];
      const message: any = { role: "assistant", api: model.api, provider: model.provider, model: model.id,
        content: calls.length ? calls : [{ type: "text", text: "Batch complete" }], stopReason: calls.length ? "toolUse" : "stop", timestamp: Date.now(),
        usage: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0, totalTokens: 15, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } };
      queueMicrotask(() => { stream.push({ type: "start", partial: message }); stream.push({ type: "done", reason: message.stopReason, message }); stream.end(); });
      return stream;
    },
  });
}
