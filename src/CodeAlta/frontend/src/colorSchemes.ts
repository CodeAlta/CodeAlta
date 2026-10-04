import { generatedColorSchemes, type ColorSchemeSwatch } from "./colorSchemes.gen";

/** A color scheme of the window: the default is Blueprint's own palette, the others redefine it. */
export type ColorScheme = Readonly<{ id: string; name: string; dark: ColorSchemeSwatch; light: ColorSchemeSwatch }>;

export const defaultColorScheme = "blueprint";
export const colorSchemeStorageKey = "codealta.desktop.colorScheme.v1";

/** Blueprint first, then the generated schemes in the order of their generator. */
export const colorSchemes: readonly ColorScheme[] = [
  { id: defaultColorScheme, name: "Blueprint",
    dark: { background: "#1c2127", surface: "#2f343c", tint: "#5f6b7c", foreground: "#f6f7f9", accent: "#4c90f0" },
    light: { background: "#f6f7f9", surface: "#ffffff", tint: "#abb3bf", foreground: "#1c2127", accent: "#2d72d2" } },
  ...generatedColorSchemes,
];
export const colorSchemeIds: readonly string[] = colorSchemes.map(scheme => scheme.id);

/** The value of the root's `data-color-scheme` attribute for a scheme; Blueprint's own palette needs none. */
export function colorSchemeAttribute(id: string): string | undefined {
  return id !== defaultColorScheme && colorSchemeIds.includes(id) ? id : undefined;
}
