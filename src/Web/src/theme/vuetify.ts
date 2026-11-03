import '@fontsource/jetbrains-mono/400.css'
import '@fontsource/jetbrains-mono/700.css'
import '@mdi/font/css/materialdesignicons.css'
import 'vuetify/styles'
import { createVuetify, type ThemeDefinition } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'
import { colors, environmentColors, font, radii, scrollbar, type ThemeName } from './tokens'

function definition(name: ThemeName): ThemeDefinition {
  const c = colors[name]
  const envs = Object.fromEntries(environmentColors[name].map((hex, i) => [`env-${i}`, hex]))
  return {
    dark: name === 'dark',
    colors: {
      background: c.background,
      surface: c.surface,
      'surface-light': c.surfaceLight,
      'surface-bright': c.surfaceBright,
      primary: c.primary,
      'on-primary': c.onPrimary,
      secondary: c.secondary,
      neutral: c.neutral,
      muted: c.muted,
      error: c.error,
      success: c.success,
      info: c.info,
      warning: c.warning,
      'input-background': c.inputBackground,
      ...envs,
    },
  }
}

export const themeConfig = (defaultTheme: ThemeName = 'dark') => ({
  defaultTheme,
  themes: { dark: definition('dark'), light: definition('light') },
})

/** Outlined is reserved for editable inputs; buttons are tonal (or text inside bars), cards flat. */
export const componentDefaults = {
  VBtn: { variant: 'tonal' },
  VAppBar: { flat: true, VBtn: { variant: 'text' } },
  VCard: { variant: 'flat' },
  VExpansionPanels: { flat: true },
  VTextField: { variant: 'outlined' },
  VTextarea: { variant: 'outlined' },
  VAutocomplete: { variant: 'outlined' },
  VCombobox: { variant: 'outlined' },
  VSelect: { variant: 'outlined' },
} as const

/** Global CSS derived only from tokens and Vuetify theme variables. */
export function globalCss(): string {
  return `
html, body, .v-application { font-family: ${font.family} !important; }
.v-card, .v-expansion-panels { border-radius: ${radii.card}; }
.v-field { background: rgb(var(--v-theme-input-background)); border-radius: ${radii.input}; }
.v-field--variant-outlined .v-field__outline { color: rgb(var(--v-theme-on-surface)); }
.v-field--variant-outlined.v-field--focused .v-field__outline { color: rgb(var(--v-theme-secondary)); }
.v-field--variant-outlined .v-field__outline__start { border-radius: ${radii.input} 0 0 ${radii.input}; }
.v-field--variant-outlined .v-field__outline__end { border-radius: 0 ${radii.input} ${radii.input} 0; }
.v-field.v-field--error .v-field__outline,
.v-field--variant-outlined.v-field--focused.v-field--error .v-field__outline { color: rgb(var(--v-theme-error)); }
.v-field--focused .v-label.v-field-label { color: rgb(var(--v-theme-secondary)); }
.v-field--error .v-label.v-field-label { color: rgb(var(--v-theme-error)); }
* { scrollbar-width: thin; scrollbar-color: rgba(var(--v-theme-on-surface), 0.35) transparent; }
*::-webkit-scrollbar { width: ${scrollbar.size}; height: ${scrollbar.size}; }
*::-webkit-scrollbar-track { background: transparent; }
*::-webkit-scrollbar-thumb { background: rgba(var(--v-theme-on-surface), 0.35); border-radius: ${scrollbar.radius}; }
.font-mono { font-family: ${font.family}; }
.v-btn-toggle .v-btn:not(.v-btn--active) { color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity)); }
.v-btn-toggle .v-btn.v-btn--active { background-color: rgba(var(--v-theme-on-surface), 0.16); font-weight: 700; }
`
}

export function installGlobalCss(doc: Document = document): void {
  const el = doc.createElement('style')
  el.setAttribute('data-app-theme', '')
  el.textContent = globalCss()
  doc.head.appendChild(el)
}

export function createAppVuetify(defaultTheme: ThemeName = 'dark') {
  installGlobalCss()
  return createVuetify({
    components,
    directives,
    theme: themeConfig(defaultTheme),
    defaults: componentDefaults,
  })
}
