// Tool approvals are enforced in the parent and in PiStation's child sessions.
export const permissionModes = ["supervised", "auto-accept-edits", "auto", "full-access"];
export function setPermissionMode(mode: string) {
  if (!permissionModes.includes(mode)) throw new Error("Unsupported PiStation permission mode.");
  process.env.PISTATION_PERMISSION_MODE = mode;
}
export function getPermissionMode() { return process.env.PISTATION_PERMISSION_MODE ?? "full-access"; }
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
