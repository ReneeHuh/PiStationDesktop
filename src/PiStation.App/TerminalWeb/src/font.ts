const PROBE_GLYPHS = ["i", "M", "0", "@", " "] as const;
const ADVANCE_TOLERANCE = 0.05;

function normalizeFontFamilies(input: string): string | null {
  const families = input
    .split(",")
    .map((name) => name.trim())
    .filter((name) => name.length > 0);
  return families.length > 0 ? families.join(", ") : null;
}

export function isMonospaceFamily(family: string): boolean {
  const families = normalizeFontFamilies(family);
  if (families === null) return true;
  try {
    const context = document.createElement("canvas").getContext("2d");
    if (context === null) return true;
    for (const variant of ["normal 400", "normal 700", "italic 400", "italic 700"]) {
      context.font = `${variant} 32px ${families}, monospace`;
      const widths = PROBE_GLYPHS.map((glyph) => context.measureText(glyph).width);
      const reference = widths[0] ?? 0;
      if (reference <= 0 || widths.some((width) => Math.abs(width - reference) >= ADVANCE_TOLERANCE)) {
        return false;
      }
    }
    return true;
  } catch {
    return true;
  }
}
