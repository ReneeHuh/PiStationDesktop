import { build } from "esbuild";
import { resolve } from "node:path";

const result = await build({
  entryPoints: [resolve("tests/search.test.ts")],
  bundle: true,
  format: "esm",
  platform: "node",
  target: "node22",
  write: false,
});
const source = result.outputFiles[0]?.text;
if (!source) throw new Error("Terminal web tests did not produce a bundle");
await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);
