import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { writeFile } from "node:fs/promises";
import browser from "./pistation-browser.ts";

// Exercise the actual bundled bridge without a model or native UI.
export default function (pi: ExtensionAPI) {
  let tool: any;
  browser({ registerTool: (registered: any) => { tool = registered; } } as ExtensionAPI);
  pi.registerCommand("browser-probe", {
    description: "Offline browser bridge probe",
    handler: async (args) => {
      const result = await tool.execute("probe", JSON.parse(args), new AbortController().signal);
      await writeFile(process.env.PISTATION_BROWSER_PROBE_RESULT!, JSON.stringify(result));
    },
  });
}
