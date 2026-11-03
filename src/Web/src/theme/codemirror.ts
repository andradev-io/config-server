import { HighlightStyle, syntaxHighlighting } from '@codemirror/language'
import type { Extension } from '@codemirror/state'
import { EditorView } from '@codemirror/view'
import { tags as t } from '@lezer/highlight'
import { editor, font, type ThemeName } from './tokens'

/** CodeMirror theme ("barf" for dark, a matching variant for light) built from tokens. */
export function codeMirrorTheme(name: ThemeName): Extension {
  const p = editor[name]
  const view = EditorView.theme(
    {
      '&': { color: p.foreground, backgroundColor: p.background, fontFamily: font.family },
      '.cm-scroller': { fontFamily: font.family },
      '.cm-content': { caretColor: p.caret },
      '.cm-cursor, .cm-dropCursor': { borderLeftColor: p.caret },
      '&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection':
        { backgroundColor: p.selection },
      '.cm-activeLine': { backgroundColor: p.lineHighlight },
      '.cm-activeLineGutter': { backgroundColor: p.lineHighlight },
      '.cm-gutters': {
        backgroundColor: p.gutterBackground,
        color: p.gutterForeground,
        border: 'none',
      },
      '.cm-panels': { backgroundColor: p.gutterBackground, color: p.foreground },
      '.cm-tooltip': { backgroundColor: p.gutterBackground, color: p.foreground, border: 'none' },
    },
    { dark: name === 'dark' },
  )
  const highlight = HighlightStyle.define([
    { tag: t.comment, color: p.comment },
    { tag: [t.string, t.regexp, t.special(t.brace)], color: p.string },
    { tag: t.number, color: p.number },
    { tag: t.bool, color: p.bool },
    { tag: [t.definitionKeyword, t.modifier, t.function(t.propertyName)], color: p.definitionKeyword, fontWeight: 'bold' },
    { tag: [t.keyword, t.moduleKeyword, t.operatorKeyword, t.operator], color: p.keyword, fontWeight: 'bold' },
    { tag: [t.variableName, t.attributeName], color: p.variableName },
    { tag: [t.function(t.variableName), t.definition(t.propertyName), t.propertyName, t.derefOperator], color: p.property },
    { tag: t.tagName, color: p.tagName },
  ])
  return [view, syntaxHighlighting(highlight)]
}
