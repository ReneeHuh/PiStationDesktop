import { createAssistantMessageEventStream } from "@earendil-works/pi-ai/compat";

export default function (pi: any) {
  pi.registerProvider("pistation-agents-offline", {
    baseUrl: "http://127.0.0.1:1", apiKey: "offline", api: "pistation-agents-test",
    models: [{ id: "deterministic", name: "Agents fixture", reasoning: false, input: ["text"],
      cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 100000, maxTokens: 1000 }],
    streamSimple(model: any, context: any, options: any) {
      const stream = createAssistantMessageEventStream();
      const marker = "PISTATION_AGENT_WORKFLOW\n";
      const userPrompts = [...context.messages].reverse().filter((message: any) => message.role === "user").map((message: any) =>
        typeof message.content === "string" ? message.content : (message.content ?? []).map((block: any) => block.text ?? "").join(""));
      const prompt = userPrompts.find((text: string) => text.includes(marker)) ?? userPrompts[0] ?? "";
      const result = context.messages.at(-1)?.role === "toolResult";
      const parent = prompt.includes(marker);
      let content: any[] = [{ type: "text", text: parent ? "Workflow reported." : "Result: " + prompt }];
      if (parent && !result) content = [{ type: "toolCall", id: "agents-" + Date.now(), name: "pistation_subagent", arguments: JSON.parse(prompt.slice(prompt.indexOf(marker) + marker.length)) }];
      if (!parent && !result && (prompt.includes("READ") || prompt.includes("WAIT"))) content = [{ type: "toolCall", id: "read-child", name: "read", arguments: { path: "README.md" } }];
      if (!parent && !result && prompt.includes("WRITE")) content = [{ type: "toolCall", id: "write-child", name: "write", arguments: { path: "child-write.txt", content: "child mutation" } }];
      if (!parent && prompt.includes("CONTINUE")) content = [{ type: "text", text: context.messages.some((m: any) => JSON.stringify(m.content).includes("Result: READ")) ? "Remembered the previous child result." : "Missing child history." }];
      const message: any = { role: "assistant", api: model.api, provider: model.provider, model: model.id, content,
        stopReason: content[0].type === "toolCall" ? "toolUse" : "stop", timestamp: Date.now(),
        usage: { input: 10, output: 5, cacheRead: 0, cacheWrite: 0, totalTokens: 15, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } };
      if (!parent && prompt.includes("FAIL")) { message.stopReason = "error"; message.errorMessage = "Deterministic child failure"; }
      if (!parent && prompt.includes("WAIT") && result) {
        const abort = () => { message.stopReason = "aborted"; message.content = []; stream.push({ type: "error", reason: "aborted", error: message }); stream.end(); };
        if (options?.signal?.aborted) abort(); else options?.signal?.addEventListener("abort", abort, { once: true });
      } else queueMicrotask(() => {
        stream.push({ type: "start", partial: message });
        stream.push(message.stopReason === "error" ? { type: "error", reason: "error", error: message } : { type: "done", reason: message.stopReason, message });
        stream.end();
      });
      return stream;
    },
  });
}
