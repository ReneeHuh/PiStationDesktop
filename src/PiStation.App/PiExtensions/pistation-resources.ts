// Desktop management uses Pi's public SDK and reports metadata, never credential values.
import { createHash, randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { DefaultPackageManager, getAgentDir, ProjectTrustStore, SettingsManager } from "@earendil-works/pi-coding-agent";

const commandName = "pistation-desktop-resources";
const kinds = ["extensions", "skills", "prompts"] as const;
const limit = (value: unknown, count = 4096) => String(value ?? "").slice(0, count);
const hash = (value: string) => createHash("sha256").update(value).digest("hex");
const key = (kind: string, path: string) => kind + ":" + resolve(path).toLowerCase();
function readConfiguration(path: string) {
  if (!existsSync(path)) return { text: "", json: {} as any };
  if (statSync(path).size > 256 * 1024) throw new Error("Pi configuration exceeds the 256 KiB limit.");
  const text = readFileSync(path, "utf8").replace(/^\uFEFF/, "");
  let json;
  // Pi accepts JSON comments. Preserve string literals such as endpoint URLs.
  const uncommented = text.replace(/"(?:\\.|[^"\\])*"|\/\/[^\r\n]*|\/\*[\s\S]*?\*\//g,
    value => value.startsWith('"') ? value : " ");
  try { json = JSON.parse(uncommented); }
  catch { throw new Error("Pi configuration is not valid JSON. Repair the file before saving changes."); }
  if (!json || typeof json !== "object" || Array.isArray(json)) throw new Error("Pi configuration must be a JSON object.");
  return { text, json };
}
function requireRevision(actual: string, expected: unknown) {
  if (actual !== expected) throw new Error("Pi settings changed. Refresh the resource panel before saving again.");
}

export default function (pi: any) {
  pi.registerCommand(commandName, {
    description: "PiStation resource and provider management",
    handler: async (argumentsText: string, ctx: any) => {
      let request: any;
      try {
        if (argumentsText.length > 96 * 1024) throw new Error("Management request is too large.");
        request = JSON.parse(Buffer.from(argumentsText, "base64url").toString("utf8"));
        if (!/^[a-f0-9]{32}$/.test(request.id)) throw new Error("Invalid management request identity.");
        if (!ctx.isIdle() || ctx.hasPendingMessages()) throw new Error("Finish the active turn before managing Pi.");
        const agentDir = getAgentDir();
        const trusted = ctx.isProjectTrusted();
        const settings = SettingsManager.create(ctx.cwd, agentDir, { projectTrusted: trusted });
        const settingsErrors = settings.drainErrors();
        const packageManager = new DefaultPackageManager({ cwd: ctx.cwd, agentDir, settingsManager: settings });
        const missing: string[] = [];
        // Inventory must not install packages or execute their extension entry points.
        const resolved = await packageManager.resolve(async (source: string) => { missing.push(source); return "skip"; });
        const resources: any[] = [];
        const confirmed = new Set<string>();
        for (const command of pi.getCommands()) {
          if (command.sourceInfo?.path) confirmed.add(key(command.source === "skill" ? "skills" : command.source === "prompt" ? "prompts" : "extensions", command.sourceInfo.path));
        }
        for (const tool of pi.getAllTools()) {
          if (tool.sourceInfo?.path) confirmed.add(key("extensions", tool.sourceInfo.path));
        }
        const promptOptions = ctx.getSystemPromptOptions();
        for (const skill of promptOptions.skills ?? []) confirmed.add(key("skills", skill.filePath));
        for (const kind of kinds) {
          for (const resource of resolved[kind] ?? []) {
            const path = resource.path;
            const file = kind === "skills" && existsSync(join(path, "SKILL.md")) ? join(path, "SKILL.md") : path;
            const metadata = resource.metadata;
            const scope = metadata.scope === "project" ? "project" : "user";
            const currentSettings = scope === "project" ? settings.getProjectSettings() : settings.getGlobalSettings();
            resources.push({
              id: key(kind, path), kind, name: basename(kind === "skills" ? dirname(file) : file), path: file,
              source: limit(metadata.source), scope, enabled: !!resource.enabled,
              confirmedLoaded: confirmed.has(key(kind, file)), canToggle: scope !== "project" || trusted,
              revision: hash(JSON.stringify(currentSettings)),
              metadata, originalPath: path,
            });
          }
        }
        // Explicit CLI resources may not be in package discovery. Keep their effective identity.
        for (const command of pi.getCommands()) {
          const info = command.sourceInfo;
          if (!info?.path || command.name === commandName) continue;
          const kind = command.source === "skill" ? "skills" : command.source === "prompt" ? "prompts" : "extensions";
          const id = key(kind, info.path);
          if (resources.some(resource => key(resource.kind, resource.path) === id)) continue;
          resources.push({ id, kind, name: command.name, path: info.path, source: limit(info.source),
            scope: info.scope ?? "temporary", enabled: true, confirmedLoaded: true, canToggle: false, revision: "" });
        }
        for (const tool of pi.getAllTools()) {
          const info = tool.sourceInfo;
          if (!info?.path || !isAbsolute(info.path)) continue;
          const id = key("extensions", info.path);
          if (!resources.some(resource => key(resource.kind, resource.path) === id)) resources.push({
            id, kind: "extensions", name: basename(info.path), path: info.path, source: limit(info.source),
            scope: info.scope ?? "temporary", enabled: true, confirmedLoaded: true, canToggle: false, revision: "",
          });
        }
        for (const file of promptOptions.contextFiles ?? []) resources.push({
          id: key("context", file.path), kind: "context", name: basename(file.path), path: file.path,
          source: "Pi system prompt", scope: "effective", enabled: true, confirmedLoaded: true, canToggle: false, revision: "",
        });

        let message = "Loaded state reflects this runtime; saved changes apply after restart.";
        if (request.action === "toggle") {
          if (settingsErrors.length) throw new Error("Fix the Pi settings errors before changing resources.");
          const resource = resources.find(item => item.id === request.resourceId);
          if (!resource?.canToggle || typeof request.enabled !== "boolean") throw new Error("This resource must be managed through its explicit launch configuration.");
          requireRevision(resource.revision, request.revision);
          const project = resource.scope === "project";
          const current = project ? settings.getProjectSettings() : settings.getGlobalSettings();
          const kind = resource.kind;
          const base = resource.metadata.baseDir ?? (project ? join(ctx.cwd, ".pi") : agentDir);
          const pattern = relative(base, resource.originalPath).replaceAll("\\", "/");
          const update = (patterns: string[] = []) => [
            ...patterns.filter(value => value.replace(/^[!+-]/, "").replaceAll("\\", "/") !== pattern),
            (request.enabled ? "+" : "-") + pattern,
          ];
          if (resource.metadata.origin === "package") {
            const packages = structuredClone(current.packages ?? []);
            const index = packages.findIndex((item: any) => (typeof item === "string" ? item : item.source) === resource.metadata.source);
            if (index < 0) throw new Error("The owning package changed. Refresh before changing it.");
            if (typeof packages[index] === "string") packages[index] = { source: packages[index] };
            packages[index][kind] = update(packages[index][kind]);
            if (project) settings.setProjectPackages(packages); else settings.setPackages(packages);
          } else {
            const method = (project ? "setProject" : "set") + ({ extensions: "ExtensionPaths", skills: "SkillPaths", prompts: "PromptTemplatePaths" } as any)[kind];
            (settings as any)[method](update(current[kind]));
          }
          await settings.flush();
          const errors = settings.drainErrors();
          if (errors.length) throw new Error(errors.map((error: any) => error.error?.message ?? error.message ?? "Settings write failed").join("\n"));
          message = "Resource setting saved. Restart the thread to apply it.";
        } else if (request.action === "trust") {
          if (typeof request.enabled !== "boolean") throw new Error("Choose a project trust decision.");
          new ProjectTrustStore(agentDir).set(ctx.cwd, request.enabled);
          message = "Project trust decision saved for this folder. Restart the thread to apply it.";
        } else if (request.action === "saveModel") {
          const model = request.model;
          if (!model || !/^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,79}$/.test(model.providerId) ||
              ["constructor", "prototype", "__proto__"].includes(model.providerId) ||
              typeof model.modelId !== "string" || !model.modelId.trim() || model.modelId.length > 256 ||
              /[\u0000-\u001f]/.test(model.modelId)) throw new Error("Enter a valid provider and model ID.");
          const url = new URL(model.baseUrl);
          if (!["http:", "https:"].includes(url.protocol) || url.username || url.password || url.search || url.hash) throw new Error("Use an HTTP(S) endpoint without credentials, a query, or a fragment.");
          if (!["openai-completions", "openai-responses", "anthropic-messages"].includes(model.api)) throw new Error("Select a supported model API.");
          if (model.apiKeyEnvironmentVariable && !/^[A-Z_][A-Z0-9_]{0,127}$/i.test(model.apiKeyEnvironmentVariable)) throw new Error("Enter an environment-variable name, not a credential.");
          if (model.keyless && !["localhost", "127.0.0.1", "[::1]"].includes(url.hostname)) throw new Error("Keyless configuration is limited to a local endpoint.");
          const modelsPath = join(agentDir, "models.json");
          const config = readConfiguration(modelsPath);
          requireRevision(hash(config.text), request.revision);
          const providers = config.json.providers ??= {};
          if (!providers || typeof providers !== "object" || Array.isArray(providers)) throw new Error("The providers configuration is invalid.");
          const provider = providers[model.providerId] ??= {};
          if (!provider || typeof provider !== "object" || Array.isArray(provider)) throw new Error("The provider configuration is invalid.");
          provider.baseUrl = url.toString();
          provider.api = model.api;
          if (model.apiKeyEnvironmentVariable) provider.apiKey = "$" + model.apiKeyEnvironmentVariable;
          else if (model.keyless) provider.apiKey = "pistation-local";
          const models = provider.models ??= [];
          if (!Array.isArray(models)) throw new Error("The provider models configuration is invalid.");
          let entry = models.find((value: any) => value.id === model.modelId);
          if (!entry) { entry = { id: model.modelId }; models.push(entry); }
          entry.name = limit(model.displayName || model.modelId, 256);
          entry.reasoning = !!model.reasoning;
          const contents = JSON.stringify(config.json, null, 2) + "\n";
          if (Buffer.byteLength(contents) > 256 * 1024) throw new Error("Model configuration exceeds 256 KiB.");
          mkdirSync(agentDir, { recursive: true });
          const temporary = modelsPath + "." + randomUUID() + ".tmp";
          try {
            writeFileSync(temporary, contents, { encoding: "utf8", mode: 0o600 });
            requireRevision(hash(readConfiguration(modelsPath).text), request.revision);
            renameSync(temporary, modelsPath);
          } finally { if (existsSync(temporary)) unlinkSync(temporary); }
          message = "Provider and model saved. Restart the thread to load the configuration.";
        } else if (request.action !== "inspect") throw new Error("Unsupported Pi management action.");

        const providerIds = [...new Set(ctx.modelRegistry.getAll().map((model: any) => model.provider))] as string[];
        const providers = providerIds.sort().slice(0, 128).map(providerId => {
          const auth = ctx.modelRegistry.getProviderAuthStatus(providerId);
          return { providerId, displayName: limit(ctx.modelRegistry.getProviderDisplayName(providerId), 256),
            credentialConfigured: !!auth.configured, credentialSource: limit(auth.source ?? "none", 64),
            modelCount: ctx.modelRegistry.getAll().filter((model: any) => model.provider === providerId).length };
        });
        let modelsRevision = "";
        const diagnostics = settingsErrors.map((error: any) => limit(error.error?.message ?? error.message ?? "Pi settings could not be read."));
        diagnostics.push(...missing.map(source => "Package not installed: " + limit(source)));
        try { modelsRevision = hash(readConfiguration(join(agentDir, "models.json")).text); }
        catch (error) { diagnostics.push(limit((error as Error).message)); }
        if (ctx.modelRegistry.getError()) diagnostics.push(limit(ctx.modelRegistry.getError()));
        if (resources.length > 1024) diagnostics.push("Resource list truncated to 1024 entries.");
        const data = {
          agentDirectory: agentDir, projectDirectory: ctx.cwd, projectTrusted: trusted,
          savedProjectTrust: new ProjectTrustStore(agentDir).get(ctx.cwd),
          resources: resources.slice(0, 1024).map(({ metadata, originalPath, ...resource }) => resource),
          providers, diagnostics, modelsRevision, message,
        };
        ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data }));
      } catch (error) {
        if (request?.id) ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: false, error: limit((error as Error).message) }));
      }
    },
  });
}
