/** Visual identity tokens. Self-contained: no imports from the rest of the app. */

export type ThemeName = 'dark' | 'light'

export interface ColorTokens {
  /** Page background (level 0). */
  background: string
  /** Cards, app bar, navigation drawer (level 1). */
  surface: string
  /** Nested panels, table headers, sticky bars (level 2). */
  surfaceLight: string
  /** Hover / raised elements on level 2 (level 3). */
  surfaceBright: string
  /** Accent (olive). Reserved for the primary action, focus and the active navigation item. */
  primary: string
  /** Text/icon color on top of primary. */
  onPrimary: string
  /** Focus outline and focused label color (olive). */
  secondary: string
  /** High-emphasis neutral (text color) for chips and highlights that are not actions. */
  neutral: string
  /** Low-emphasis neutral for secondary chips and hints. */
  muted: string
  error: string
  success: string
  info: string
  warning: string
  inputBackground: string
}

const shared = {
  secondary: '#b4b96b',
  error: '#ff4d4d',
  success: '#b3bb4b',
  info: '#4b9bb3',
  warning: '#b3a24b',
}

export const colors: Record<ThemeName, ColorTokens> = {
  dark: {
    ...shared,
    background: '#121212',
    surface: '#1c1c1c',
    surfaceLight: '#252525',
    surfaceBright: '#2e2e2e',
    primary: '#b4b96b',
    onPrimary: '#121212',
    neutral: '#f5f5f5',
    muted: '#9a9a9a',
    inputBackground: '#222930',
  },
  light: {
    ...shared,
    background: '#f0f0f0',
    surface: '#ffffff',
    surfaceLight: '#f6f6f6',
    surfaceBright: '#ebebeb',
    // Darker shade of the olive so text/icons on white keep AA contrast.
    primary: '#6b6f2a',
    onPrimary: '#ffffff',
    neutral: '#222222',
    muted: '#6b6b6b',
    inputBackground: '#e3e8ec',
  },
}

/**
 * Environment colors (Vuetify theme colors `env-0` .. `env-7`). Slots 0-2 are the fixed colors for
 * dev / stg / prd; the others are picked by a hash of the environment name. Never olive.
 */
export const environmentColors: Record<ThemeName, readonly string[]> = {
  dark: ['#5cc0a8', '#e0a458', '#e07a8a', '#6aaee0', '#a891e0', '#d68fc6', '#c9b28a', '#7fc4cf'],
  light: ['#1f7f6a', '#a2621a', '#b23a52', '#2a6fa8', '#6a4fb3', '#9b3f8a', '#7d6638', '#2b7d8a'],
}

export const font = {
  family: '"JetBrains Mono", monospace',
}

export const radii = {
  input: '15px',
  card: '12px',
}

export const scrollbar = {
  size: '10px',
  radius: '10px',
}

/** Editor palette; dark is the site's "barf" theme, light is a matching variant. */
export interface EditorPalette {
  background: string
  foreground: string
  caret: string
  selection: string
  gutterBackground: string
  gutterForeground: string
  lineHighlight: string
  comment: string
  string: string
  number: string
  bool: string
  keyword: string
  definitionKeyword: string
  variableName: string
  property: string
  tagName: string
}

export const editor: Record<ThemeName, EditorPalette> = {
  dark: {
    background: '#15191EFA',
    foreground: '#EEF2F7',
    caret: '#C4C4C4',
    selection: '#90B2D557',
    gutterBackground: '#15191EFA',
    gutterForeground: '#aaaaaa95',
    lineHighlight: '#57575712',
    comment: '#6E6E6E',
    string: '#5C81B3',
    number: '#C1E1B8',
    bool: '#53667D',
    keyword: '#697A8E',
    definitionKeyword: '#A3D295',
    variableName: '#708E67',
    property: '#ffffff',
    tagName: '#A3D295',
  },
  light: {
    background: '#f5f5f5',
    foreground: '#222222',
    caret: '#222222',
    selection: '#90B2D566',
    gutterBackground: '#e3e8ec',
    gutterForeground: '#666666',
    lineHighlight: '#00000008',
    comment: '#7a7a7a',
    string: '#2f5d8f',
    number: '#4a7a3a',
    bool: '#3d5068',
    keyword: '#4a5a70',
    definitionKeyword: '#4a7a3a',
    variableName: '#4f6b46',
    property: '#111111',
    tagName: '#4a7a3a',
  },
}
