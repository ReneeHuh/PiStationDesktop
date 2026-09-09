// Tool approvals are enforced in the parent and in PiStation's child sessions.
export const permissionModes = ["supervised", "auto-accept-edits", "auto", "full-access"];
export function setPermissionMode(mode: string) {
  if (!permissionModes.includes(mode)) throw new Error("Unsupported PiStation permission mode.");
  process.env.PISTATION_PERMISSION_MODE = mode;
}
export function getPermissionMode() { return process.env.PISTATION_PERMISSION_MODE ?? "full-access"; }
export function getToolSelection(): { mode: number; allowed: string[]; excluded: string[] } {
  const raw = process.env.PISTATION_TOOL_SELECTION;
  if (!raw) return { mode: 0, allowed: [], excluded: [] };
  if (raw.length > 64 * 1024) throw new Error("PiStation tool selection exceeds its size limit.");
  const value = JSON.parse(raw);
  const validNames = (names: any) => names == null || Array.isArray(names) && names.length <= 128 &&
    names.every((name: any) => typeof name === "string" && /^[A-Za-z0-9_.-]{1,128}$/.test(name));
  if (!value || ![0, 1, 2].includes(value.mode) || !validNames(value.allowed) || !validNames(value.excluded))
    throw new Error("PiStation tool selection is invalid. Repair runtime settings and restart.");
  return { mode: value.mode, allowed: value.allowed ?? [], excluded: value.excluded ?? [] };
}
export function filterChildTools(tools: string[]): string[] {
  const selection = getToolSelection();
  return tools.filter(name => selection.mode !== 2 &&
    (selection.mode !== 1 || selection.allowed.includes(name)) && !selection.excluded.includes(name));
}
export async function reviewToolCall(event: any, ctx: any, source = "Pi") {
  const mode = getPermissionMode();
  if (mode === "full-access" || ["read", "grep", "find", "ls"].includes(event.toolName) ||
      mode === "auto-accept-edits" && ["edit", "write"].includes(event.toolName)) return;
  if (!ctx.hasUI) return { block: true, reason: "This permission mode requires an available desktop approval UI." };
  const approved = await ctx.ui.confirm(`${source}: allow ${event.toolName}?`,
    (mode === "auto" ? "Automatic review is unavailable for Pi; user approval is required.\n\n" : "") +
    JSON.stringify(event.input ?? {}, null, 2).slice(0, 8192));
  if (!approved) return { block: true, reason: "The user declined this tool operation." };
}
