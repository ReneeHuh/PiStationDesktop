// Desktop management uses Pi's public SDK and reports metadata, never credential values.
import { createHash, randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { DefaultPackageManager, getAgentDir, ModelRuntime, ProjectTrustStore, SettingsManager } from "@earendil-works/pi-coding-agent";
import { spawn } from "node:child_process";
import { getSupportedThinkingLevels } from "@earendil-works/pi-ai";
import { getProviders as getBuiltinProviders } from "@earendil-works/pi-ai/compat";
import { getPermissionMode, getToolSelection, permissionModes, reviewToolCall, setPermissionMode } from "./pistation-permissions.ts";
import registerSessions from "./pistation-sessions.ts";
import { registerQuotaFeeds } from "./pistation-quotas.mjs";

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
  registerSessions(pi);
  registerQuotaFeeds(pi);
  let startupTransport: string | undefined;
  pi.on("session_start", (_event: any, ctx: any) => {
    // Snapshot at startup; saving later does not pretend the existing Agent changed transport.
    startupTransport = SettingsManager.create(ctx.cwd, getAgentDir(), { projectTrusted: ctx.isProjectTrusted() }).getTransport();
  });
  pi.on("tool_call", (event: any, ctx: any) => reviewToolCall(event, ctx));
  pi.registerCommand(commandName, {
    description: "PiStation resource and provider management",
    handler: async (argumentsText: string, ctx: any) => {
      let request: any;
      try {
        if (argumentsText.length > 96 * 1024) throw new Error("Management request is too large.");
        request = JSON.parse(Buffer.from(argumentsText, "base64url").toString("utf8"));
        if (!/^[a-f0-9]{32}$/.test(request.id)) throw new Error("Invalid management request identity.");
        if (!ctx.isIdle() || ctx.hasPendingMessages()) throw new Error("Finish the active turn before managing Pi.");
        if (request.action === "capabilities" || request.action === "permission") {
          if (request.action === "permission") setPermissionMode(request.mode);
          ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data: {
            permissionMode: getPermissionMode(), permissionModes,
            models: ctx.modelRegistry.getAvailable().map((model: any) => ({ providerId: model.provider, modelId: model.id,
              thinkingLevels: getSupportedThinkingLevels(model) })),
          } }));
          return;
        }
        const sdk = (globalThis as any)[Symbol.for("pistation.sdk")];
        if (request.action === "reload") {
          if (!sdk) throw new Error("In-process desktop reload requires Pi 0.85 or later with the PiStation SDK adapter.");
          const reply = sdk.ui.context.setStatus;
          sdk.ui.reset();
          try {
            await ctx.reload();
            reply("pistation-management:" + request.id, JSON.stringify({ success: true, data: { message: "Resources reloaded in the same Pi process. Conversation preserved." } }));
          } catch (error) {
            reply("pistation-management:" + request.id, JSON.stringify({ success: false, error: "Reload failed: " + limit((error as Error).message) + ". Restart this thread to recover." }));
          }
          return;
        }
        if (request.action === "toolExecution") {
          if (!sdk) throw new Error("Tool batch controls require Pi 0.85 or later with the PiStation SDK adapter.");
          if (!["parallel", "sequential"].includes(request.toolExecution)) throw new Error("Choose parallel or sequential tool execution.");
          sdk.session.agent.toolExecution = request.toolExecution;
        }
        const agentDir = getAgentDir();
        const trusted = ctx.isProjectTrusted();
        const settings = SettingsManager.create(ctx.cwd, agentDir, { projectTrusted: trusted });
        const settingsErrors = settings.drainErrors();
        if (request.action === "automation") {
          ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true,
            data: { autoCompaction: settings.getCompactionSettings().enabled, autoRetry: settings.getRetrySettings().enabled } }));
          return;
        }
        const packageManager = new DefaultPackageManager({ cwd: ctx.cwd, agentDir, settingsManager: settings });
        packageManager.setProgressCallback((event: any) => ctx.ui.setStatus("Pi packages", limit(`${event.action}: ${event.source} · ${event.message ?? event.type}`)));
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

        const loader = sdk?.session.resourceLoader;
        if (loader) {
          const loaded = loader.getExtensions();
          for (const resource of resources.filter(item => item.kind === "extensions")) resource.confirmedLoaded = false;
          for (const entry of [...loaded.extensions.map((extension: any) => ({ path: extension.path, loaded: true })),
              ...loaded.errors.map((error: any) => ({ path: error.path, loaded: false, error: limit(error.error) }))]) {
            let resource = resources.find(item => item.kind === "extensions" && key("extensions", item.path) === key("extensions", entry.path));
            if (!resource) { resource = { id: key("extensions", entry.path), kind: "extensions", name: basename(entry.path), path: entry.path,
              source: "Pi resource loader", scope: "effective", enabled: true, canToggle: false, revision: "" }; resources.push(resource); }
            resource.confirmedLoaded = entry.loaded; resource.loadError = entry.error ?? null;
          }
        }
        let message = request.action === "toolExecution" ? "Tool execution updated for this runtime. Per-tool sequential restrictions still apply." : "Loaded state reflects this runtime; saved changes apply after reload or restart.";
        if (request.action === "saveTransport") {
          if (settingsErrors.length) throw new Error("Fix Pi settings errors before saving transport.");
          if (!["auto", "sse", "websocket", "websocket-cached"].includes(request.transport)) throw new Error("Choose auto, SSE or WebSocket transport.");
          requireRevision(hash(JSON.stringify(settings.getGlobalSettings())), request.revision);
          if (typeof settings.setTransport !== "function") throw new Error("This Pi version does not support transport preferences.");
          settings.setTransport(request.transport);
          await settings.flush();
          if (settings.drainErrors().length) throw new Error("Pi transport settings could not be saved.");
          message = "Transport saved in this host user's Pi settings. Trusted project overrides still win. Restart each thread to apply it.";
        } else if (request.action === "toggle") {
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
        } else if (["packageInstall", "packageRemove", "packageUpdate"].includes(request.action)) {
          if (settingsErrors.length) throw new Error("Fix Pi settings errors before changing packages.");
          if (request.packageLocal && !trusted) throw new Error("Trust this project before changing its packages.");
          const source = request.packageSource;
          if (typeof source !== "string" || !source.trim() || source.length > 4096 || /[\u0000-\u001f]/.test(source)) throw new Error("Enter a package source such as npm:package, a Git URL, or a local path.");
          const options = { local: !!request.packageLocal };
          if (request.action === "packageInstall") await packageManager.installAndPersist(source, options);
          else if (request.action === "packageRemove") await packageManager.removeAndPersist(source, options);
          else await packageManager.update(source);
          await settings.flush();
          const errors = settings.drainErrors();
          if (errors.length) throw new Error("The package operation ran, but Pi could not save its configuration. Refresh before retrying.");
          message = "Package operation completed. Restart the thread to load the updated resources.";
          ctx.ui.setStatus("Pi packages", message);
        } else if (request.action === "login" || request.action === "logout") {
          const providerId = request.resourceId;
          if (typeof providerId !== "string" || !ctx.modelRegistry.getProvider(providerId)) throw new Error("Choose an available Pi provider.");
          const runtime = await ModelRuntime.create({ authPath: join(agentDir, "auth.json"), modelsPath: join(agentDir, "models.json"), allowModelNetwork: false });
          for (const id of ctx.modelRegistry.getRegisteredProviderIds()) {
            const native = ctx.modelRegistry.getRegisteredNativeProvider(id);
            const provider = ctx.modelRegistry.getRegisteredProviderConfig(id);
            if (native) runtime.registerNativeProvider(native); else if (provider) runtime.registerProvider(id, provider);
          }
          if (request.action === "logout") await runtime.logout(providerId);
          else {
            const abort = new AbortController();
            const timer = setTimeout(() => abort.abort(), 8 * 60 * 1000);
            try {
              const authType = request.authType ?? "oauth";
              if (!["oauth", "api_key"].includes(authType)) throw new Error("Choose browser or API-key sign-in.");
              await runtime.login(providerId, authType, {
                signal: abort.signal,
                prompt: async (prompt: any) => {
                  const answer = prompt.type === "select" ? await ctx.ui.select(prompt.message, prompt.options.map((option: any) => option.label), { signal: abort.signal, timeout: 8 * 60 * 1000 })
                    : await ctx.ui.input(prompt.message, "[pistation:secret]", { signal: abort.signal, timeout: 8 * 60 * 1000 });
                  if (answer === undefined) { abort.abort(); throw new Error("Sign-in cancelled."); }
                  return prompt.type === "select" ? prompt.options.find((option: any) => option.label === answer)?.id ?? answer : answer;
                },
                notify: (event: any) => {
                  const url = event.type === "auth_url" ? event.url : event.verificationUri;
                  ctx.ui.setStatus("Pi sign-in", limit(event.userCode ? `Code: ${event.userCode} · ${url}` : event.instructions ?? event.message ?? url));
                  if (url && process.platform === "win32" && new URL(url).protocol === "https:") {
                    const browser = spawn("rundll32.exe", ["url.dll,FileProtocolHandler", url], { windowsHide: true, stdio: "ignore" });
                    browser.on("error", () => ctx.ui.notify("Open this sign-in URL: " + url, "info")); browser.unref();
                  }
                },
              });
            } finally { clearTimeout(timer); }
          }
          await ctx.modelRegistry.refresh({ allowNetwork: false });
          message = request.action === "login" ? "Signed in. Provider credentials refreshed." : "Signed out of stored Pi credentials. Environment or ambient credentials may remain configured.";
          ctx.ui.setStatus("Pi sign-in", message);
        } else if (request.action !== "inspect" && request.action !== "toolExecution") throw new Error("Unsupported Pi management action.");

        const providerIds = [...new Set([...(sdk?.session.modelRuntime.getProviders() ?? getBuiltinProviders()).map((provider: any) => typeof provider === "string" ? provider : provider.id),
          ...ctx.modelRegistry.getAll().map((model: any) => model.provider)].filter((id: any) => typeof id === "string" && id.length > 0))] as string[];
        const providers = providerIds.sort().slice(0, 128).map(providerId => {
          const auth = ctx.modelRegistry.getProviderAuthStatus(providerId);
          return { providerId, displayName: limit(ctx.modelRegistry.getProviderDisplayName(providerId), 256),
            credentialConfigured: !!auth.configured, credentialSource: limit(auth.source ?? "none", 64),
            modelCount: ctx.modelRegistry.getAll().filter((model: any) => model.provider === providerId).length,
            supportsOAuth: !!ctx.modelRegistry.getProvider(providerId)?.auth?.oauth?.login,
            supportsApiKey: !!ctx.modelRegistry.getProvider(providerId)?.auth?.apiKey?.login };
        });
        let modelsRevision = "";
        const diagnostics = settingsErrors.map((error: any) => limit(error.error?.message ?? error.message ?? "Pi settings could not be read."));
        for (const diagnostic of [...(loader?.getSkills().diagnostics ?? []), ...(loader?.getPrompts().diagnostics ?? [])]) diagnostics.push(limit(`${diagnostic.path ?? "resource"}: ${diagnostic.message}`));
        diagnostics.push(...missing.map(source => "Package not installed: " + limit(source)));
        try { modelsRevision = hash(readConfiguration(join(agentDir, "models.json")).text); }
        catch (error) { diagnostics.push(limit((error as Error).message)); }
        if (ctx.modelRegistry.getError()) diagnostics.push(limit(ctx.modelRegistry.getError()));
        if (resources.length > 1024) diagnostics.push("Resource list truncated to 1024 entries.");
        const activeTools = new Set<string>(pi.getActiveTools());
        const allTools = pi.getAllTools();
        const toolInventory = {
          selection: getToolSelection(), truncated: allTools.length > 256,
          tools: allTools.slice(0, 256).map((tool: any) => ({ name: limit(tool.name, 128),
            description: limit(tool.description, 240),
            source: limit(tool.sourceInfo?.source ?? "unattributed", 256), active: activeTools.has(tool.name) })),
        };
        if (toolInventory.truncated) diagnostics.push("Tool inventory truncated to 256 registered entries.");
        const data = {
          agentDirectory: agentDir, projectDirectory: ctx.cwd, projectTrusted: trusted,
          savedProjectTrust: new ProjectTrustStore(agentDir).get(ctx.cwd),
          resources: resources.slice(0, 1024).map(({ metadata, originalPath, ...resource }) => resource),
          nativePreferences: {
            savedTransport: settings.getGlobalSettings().transport ?? "auto",
            transportRevision: hash(JSON.stringify(settings.getGlobalSettings())),
            projectTransport: settings.getProjectSettings().transport ?? null, startupTransport: startupTransport ?? null,
            cacheRetention: process.env.PI_CACHE_RETENTION === "long" ? "long" : "short (provider default)",
            telemetry: process.env.PI_TELEMETRY === undefined ? (settings.getEnableInstallTelemetry() ? "enabled (Pi settings)" : "disabled (Pi settings)") : (/^(1|true|yes)$/i.test(process.env.PI_TELEMETRY) ? "enabled (environment)" : "disabled (environment)"),
            offline: /^(1|true|yes)$/i.test(process.env.PI_OFFLINE ?? ""),
            skipVersionCheck: !!process.env.PI_SKIP_VERSION_CHECK,
          },
          authoritativeResources: !!loader, toolExecution: sdk?.session.agent.toolExecution ?? null, supportsDesktopComponents: !!sdk,
          providers, diagnostics, modelsRevision, message, packages: packageManager.listConfiguredPackages(), toolInventory,
        };
        ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: true, data }));
      } catch (error) {
        if (request?.id) ctx.ui.setStatus("pistation-management:" + request.id, JSON.stringify({ success: false, error: limit((error as Error).message) }));
      }
    },
  });
}
