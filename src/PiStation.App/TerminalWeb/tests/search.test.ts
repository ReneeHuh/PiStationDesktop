import assert from "node:assert/strict";
import test from "node:test";
import type { GhosttyCell, GhosttyRow } from "../src/ghostty/core";
import {
  buildGhosttySearchDocument,
  findGhosttySearchMatches,
} from "../src/ghostty/search";
import {
  formatCommandGesture,
  resolveCommandShortcut,
  type TerminalCommandShortcutKey,
} from "../src/shortcuts";

function cell(text: string, wide = 0): GhosttyCell {
  return {
    text,
    wide,
    foreground: { r: 255, g: 255, b: 255 },
    background: { r: 0, g: 0, b: 0 },
    bold: false,
    italic: false,
    invisible: false,
    strikethrough: false,
    overline: false,
    underline: false,
    selected: false,
  };
}

function row(cells: readonly GhosttyCell[], wrapsToNext = false): GhosttyRow {
  return { cells, text: "", isWrapContinuation: false, wrapsToNext };
}

test("builds a coordinate map across hard lines and soft wraps", () => {
  const document = buildGhosttySearchDocument([
    row([cell("a"), cell("b"), cell("")]),
    row([cell("c"), cell("d")], true),
    row([cell("e"), cell("f")]),
  ]);

  assert.equal(document.text, "ab\ncdef");
  assert.deepEqual(document.points[3], { x: 0, y: 1 });
  assert.deepEqual(document.points[6], { x: 1, y: 2 });
});

test("omits a wide grapheme spacer cell without losing its coordinate", () => {
  const document = buildGhosttySearchDocument([
    row([cell("🙂"), cell("", 2), cell("x")]),
  ]);

  assert.equal(document.text, "🙂x");
  assert.deepEqual(document.points[0], { x: 0, y: 0 });
  assert.deepEqual(document.points[1], { x: 0, y: 0 });
  assert.deepEqual(document.points[2], { x: 2, y: 0 });
});

test("finds literal matches with case and whole-word controls", () => {
  const text = "Alpha alphabet ALPHA";
  const document = {
    text,
    points: Array.from({ length: text.length }, (_, x) => ({ x, y: 0 })),
  };

  assert.equal(
    findGhosttySearchMatches(document, "alpha", { caseSensitive: false, wholeWord: false }).length,
    3,
  );
  assert.equal(
    findGhosttySearchMatches(document, "alpha", { caseSensitive: false, wholeWord: true }).length,
    2,
  );
  assert.equal(
    findGhosttySearchMatches(document, "Alpha", { caseSensitive: true, wholeWord: true }).length,
    1,
  );
});

function shortcutKey(
  code: string,
  modifiers: Partial<Omit<TerminalCommandShortcutKey, "code">> = {},
): TerminalCommandShortcutKey {
  return {
    code,
    ctrlKey: false,
    shiftKey: false,
    altKey: false,
    metaKey: false,
    ...modifiers,
  };
}

test("formats canonical command gestures", () => {
  assert.equal(
    formatCommandGesture(shortcutKey("Digit5", { ctrlKey: true, shiftKey: true })),
    "Ctrl+Shift+Number5",
  );
  assert.equal(
    formatCommandGesture(shortcutKey("ArrowLeft", { altKey: true })),
    "Alt+Left",
  );
  assert.equal(
    formatCommandGesture(shortcutKey("KeyB", { ctrlKey: true, altKey: true })),
    "Ctrl+Alt+B",
  );
});

test("forwards only gestures published by the command registry", () => {
  const gestures = new Set(["Ctrl+K", "Alt+Right"]);
  assert.equal(
    resolveCommandShortcut(shortcutKey("KeyK", { ctrlKey: true }), gestures),
    "Ctrl+K",
  );
  assert.equal(
    resolveCommandShortcut(shortcutKey("ArrowRight", { altKey: true }), gestures),
    "Alt+Right",
  );
  assert.equal(
    resolveCommandShortcut(shortcutKey("KeyW", { ctrlKey: true, shiftKey: true }), gestures),
    null,
  );
});
