export interface TerminalCommandShortcutKey {
  readonly code: string;
  readonly ctrlKey: boolean;
  readonly shiftKey: boolean;
  readonly altKey: boolean;
  readonly metaKey: boolean;
}

const codeAliases: Readonly<Record<string, string>> = {
  ArrowDown: "Down",
  ArrowLeft: "Left",
  ArrowRight: "Right",
  ArrowUp: "Up",
  Escape: "Esc",
};

export function formatCommandGesture(event: TerminalCommandShortcutKey): string | null {
  const code = event.code;
  let key: string | undefined;
  if (/^Key[A-Z]$/u.test(code)) key = code.slice(3);
  else if (/^Digit[0-9]$/u.test(code)) key = `Number${code.slice(5)}`;
  else if (/^Numpad[0-9]$/u.test(code)) key = `NumberPad${code.slice(6)}`;
  else if (/^F(?:[1-9]|1[0-9]|2[0-4])$/u.test(code)) key = code;
  else if (code === "Comma") key = "Comma";
  else key = codeAliases[code] ?? code;
  if (!key || key === "Unidentified") return null;

  const parts: string[] = [];
  if (event.ctrlKey) parts.push("Ctrl");
  if (event.altKey) parts.push("Alt");
  if (event.shiftKey) parts.push("Shift");
  if (event.metaKey) parts.push("Win");
  parts.push(key);
  return parts.join("+");
}

export function resolveCommandShortcut(
  event: TerminalCommandShortcutKey,
  gestures: ReadonlySet<string>,
): string | null {
  const gesture = formatCommandGesture(event);
  return gesture && gestures.has(gesture) ? gesture : null;
}
