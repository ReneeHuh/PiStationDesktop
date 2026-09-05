import { build } from "esbuild";
import { copyFile, mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const output = resolve(root, "dist");
await mkdir(output, { recursive: true });
await build({
  entryPoints: [resolve(root, "src/bridge.ts")],
  outfile: resolve(output, "terminal.js"),
  bundle: true,
  format: "iife",
  platform: "browser",
  target: "chrome120",
  minify: true,
  legalComments: "none",
  assetNames: "[name]",
  loader: {
    ".wasm": "file",
    ".woff2": "file",
  },
});
await copyFile(resolve(root, "src/index.html"), resolve(output, "index.html"));
