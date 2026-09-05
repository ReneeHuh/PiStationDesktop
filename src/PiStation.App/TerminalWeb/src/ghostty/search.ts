import type { GhosttyRow } from "./core";

export interface GhosttySearchPoint {
  readonly x: number;
  readonly y: number;
}

export interface GhosttySearchMatch {
  readonly start: GhosttySearchPoint;
  readonly end: GhosttySearchPoint;
}

export interface GhosttySearchDocument {
  readonly text: string;
  readonly points: readonly (GhosttySearchPoint | null)[];
}

export interface GhosttySearchOptions {
  readonly caseSensitive: boolean;
  readonly wholeWord: boolean;
}

const WORD_CHARACTER = /[\p{L}\p{N}_]/u;

function isWordCharacter(value: string | undefined): boolean {
  return value !== undefined && WORD_CHARACTER.test(value);
}

function codePointBefore(value: string, index: number): string | undefined {
  if (index <= 0) return undefined;
  const low = value.charCodeAt(index - 1);
  const start = low >= 0xdc00 && low <= 0xdfff && index >= 2 ? index - 2 : index - 1;
  return String.fromCodePoint(value.codePointAt(start) ?? low);
}

function codePointAt(value: string, index: number): string | undefined {
  if (index >= value.length) return undefined;
  return String.fromCodePoint(value.codePointAt(index) ?? value.charCodeAt(index));
}

/**
 * Builds searchable text from rows emitted by Ghostty. Empty spacer cells that
 * trail a wide grapheme are omitted, while normal blank cells remain searchable
 * spaces. Hard line endings become newlines and soft wraps remain contiguous.
 */
export function buildGhosttySearchDocument(
  rows: readonly (GhosttyRow | undefined)[],
): GhosttySearchDocument {
  let text = "";
  const points: Array<GhosttySearchPoint | null> = [];

  for (let rowIndex = 0; rowIndex < rows.length; rowIndex += 1) {
    const row = rows[rowIndex];
    if (row) {
      const chunks = row.cells.map((cell, column) => ({
        column,
        value: cell.text.length > 0 ? cell.text : cell.wide === 2 ? "" : " ",
      }));
      if (!row.wrapsToNext) {
        while (chunks.length > 0 && chunks.at(-1)?.value === " ") chunks.pop();
      }
      for (const chunk of chunks) {
        text += chunk.value;
        for (let index = 0; index < chunk.value.length; index += 1) {
          points.push({ x: chunk.column, y: rowIndex });
        }
      }
    }

    if (!row?.wrapsToNext && rowIndex < rows.length - 1) {
      text += "\n";
      points.push(null);
    }
  }

  return { text, points };
}

export function findGhosttySearchMatches(
  document: GhosttySearchDocument,
  query: string,
  options: GhosttySearchOptions,
): readonly GhosttySearchMatch[] {
  if (query.length === 0) return [];
  const escaped = query.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const expression = new RegExp(escaped, options.caseSensitive ? "gu" : "giu");
  const matches: GhosttySearchMatch[] = [];

  for (const candidate of document.text.matchAll(expression)) {
    const index = candidate.index;
    const value = candidate[0];
    if (index === undefined || value.length === 0) continue;
    if (
      options.wholeWord &&
      ((isWordCharacter(codePointAt(value, 0)) &&
        isWordCharacter(codePointBefore(document.text, index))) ||
        (isWordCharacter(codePointBefore(value, value.length)) &&
          isWordCharacter(codePointAt(document.text, index + value.length))))
    ) {
      continue;
    }
    const start = document.points[index];
    const end = document.points[index + value.length - 1];
    if (start && end) matches.push({ start, end });
  }

  return matches;
}
