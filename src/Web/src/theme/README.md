# theme

Self-contained visual identity (no imports from the rest of the app; meant to be extracted into a shared package).

- `tokens.ts` - `colors` (dark/light), `environmentColors` (8 environment slots per theme), `font`, `radii` (input, card), `scrollbar`, `editor` (CodeMirror palettes), types `ThemeName`, `ColorTokens`, `EditorPalette`.
- `vuetify.ts` - `createAppVuetify(defaultTheme)`, `themeConfig`, `componentDefaults`, `globalCss`, `installGlobalCss`. Also loads the JetBrains Mono font, MDI icons and Vuetify styles.
- `codemirror.ts` - `codeMirrorTheme(name)`: CodeMirror extension (editor theme + syntax highlighting); dark is the site's "barf".

Components read colors via Vuetify theme variables (`rgb(var(--v-theme-...))`) or these exports; no hex values elsewhere.

## Colour roles

| Theme colour | Dark | Light | Use |
| --- | --- | --- | --- |
| `background` | `#121212` | `#f0f0f0` | page (level 0) |
| `surface` | `#1c1c1c` | `#ffffff` | cards, app bar, drawer (level 1) |
| `surface-light` | `#252525` | `#f6f6f6` | nested panels, table headers (level 2) |
| `surface-bright` | `#2e2e2e` | `#ebebeb` | raised bars (sticky pending bar) (level 3) |
| `primary` | `#b4b96b` | `#6b6f2a` | olive accent: **only** the primary action, the active navigation item and active controls (switches) |
| `secondary` | `#b4b96b` | `#b4b96b` | focus outline and focused label of inputs |
| `neutral` | `#f5f5f5` | `#222222` | high-emphasis chips/highlights that are not actions (JSON keys, "declared", "required", write permission) |
| `muted` | `#9a9a9a` | `#6b6b6b` | low-emphasis chips ("implicit", "optional") |
| `success` / `info` / `warning` / `error` | shared | shared | states |
| `input-background` | `#222930` | `#e3e8ec` | input fields |
| `env-0` .. `env-7` | lighter | darker | environments: `env-0` dev, `env-1` stg, `env-2` prd, others hashed by name (see `src/utils/envColor.ts`). Never olive. |

The light `primary` is a darker shade of the olive so text and icons on white keep AA contrast.

## Component defaults

- Outlined is reserved for editable inputs (`VTextField`, `VTextarea`, `VSelect`, `VAutocomplete`, `VCombobox`); the
  unfocused outline is neutral, the focused one olive.
- Buttons are `tonal` (`text` inside the app bar); the primary action of a screen is `color="primary" variant="flat"`.
- Cards are `flat` (no border, no shadow) with `radii.card`; expansion panels are flat. Layering comes from the
  surface colours instead of borders.
