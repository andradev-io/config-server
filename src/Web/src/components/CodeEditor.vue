<script setup lang="ts">
import { json } from '@codemirror/lang-json'
import { yaml } from '@codemirror/lang-yaml'
import { StreamLanguage } from '@codemirror/language'
import { properties } from '@codemirror/legacy-modes/mode/properties'
import { forceLinting, linter, type Diagnostic } from '@codemirror/lint'
import { Compartment, EditorState, Prec, type Extension } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { basicSetup } from 'codemirror'
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { codeMirrorTheme } from '../theme/codemirror'
import { lintText, type EditorLanguage } from '../utils/lint'

export interface ExternalMarker {
  line: number
  column?: number
  message: string
}

const props = withDefaults(
  defineProps<{
    modelValue: string
    language?: EditorLanguage
    readOnly?: boolean
    dark?: boolean
    /** Extra markers (e.g. server validation errors), 1-based lines. */
    markers?: ExternalMarker[]
    /** Disable client-side lint (used by read-only views). */
    lint?: boolean
  }>(),
  { language: 'json', readOnly: false, dark: true, markers: () => [], lint: true },
)
const emit = defineEmits<{ 'update:modelValue': [string]; save: [] }>()

const host = ref<HTMLElement>()
const view = shallowRef<EditorView>()
const langC = new Compartment()
const themeC = new Compartment()
const roC = new Compartment()

function languageExt(l: EditorLanguage): Extension {
  if (l === 'json') return json()
  if (l === 'yaml') return yaml()
  return StreamLanguage.define(properties)
}
const themeExt = () => codeMirrorTheme(props.dark ? 'dark' : 'light')
const roExt = () => [EditorState.readOnly.of(props.readOnly), EditorView.editable.of(!props.readOnly)]

function diagnostics(v: EditorView): Diagnostic[] {
  const doc = v.state.doc
  const toPos = (line: number, column = 1) => {
    const ln = doc.line(Math.max(1, Math.min(line, doc.lines)))
    return { from: Math.min(ln.from + Math.max(0, column - 1), ln.to), line: ln }
  }
  const out: Diagnostic[] = []
  const add = (line: number, column: number | undefined, message: string) => {
    const { from, line: ln } = toPos(line, column)
    const to = from < ln.to ? Math.min(from + 1, ln.to) : ln.to
    out.push({ from: Math.min(from, to), to: Math.max(from, to), severity: 'error', message })
  }
  if (props.lint) for (const i of lintText(props.language, doc.toString())) add(i.line, i.column, i.message)
  for (const m of props.markers) add(m.line, m.column, m.message)
  return out
}

onMounted(() => {
  const extensions: Extension[] = [
    basicSetup,
    langC.of(languageExt(props.language)),
    themeC.of(themeExt()),
    roC.of(roExt()),
    linter((v) => diagnostics(v), { delay: 250 }),
    Prec.highest(
      keymap.of([
        {
          key: 'Mod-s',
          preventDefault: true,
          run: () => {
            emit('save')
            return true
          },
        },
      ]),
    ),
    EditorView.updateListener.of((u) => {
      if (u.docChanged) emit('update:modelValue', u.state.doc.toString())
    }),
  ]
  view.value = new EditorView({
    parent: host.value!,
    state: EditorState.create({ doc: props.modelValue, extensions }),
  })
})

onBeforeUnmount(() => view.value?.destroy())

watch(
  () => props.modelValue,
  (val) => {
    const v = view.value
    if (v && val !== v.state.doc.toString()) {
      v.dispatch({ changes: { from: 0, to: v.state.doc.length, insert: val } })
    }
  },
)
watch(
  () => props.language,
  () => {
    view.value?.dispatch({ effects: langC.reconfigure(languageExt(props.language)) })
    if (view.value) forceLinting(view.value)
  },
)
watch(
  () => props.dark,
  () => view.value?.dispatch({ effects: themeC.reconfigure(themeExt()) }),
)
watch(
  () => props.readOnly,
  () => view.value?.dispatch({ effects: roC.reconfigure(roExt()) }),
)
watch(
  () => props.markers,
  () => view.value && forceLinting(view.value),
  { deep: true },
)
</script>

<template>
  <div ref="host" class="code-editor" />
</template>

<style>
.code-editor {
  min-width: 0;
  max-width: 100%;
  height: 100%;
  min-height: 200px;
  border-radius: 4px;
  overflow: hidden;
}
.code-editor .cm-editor {
  height: 100%;
  max-width: 100%;
}
.code-editor .cm-scroller {
  overflow: auto;
}
</style>
