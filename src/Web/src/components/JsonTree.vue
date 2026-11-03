<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { JsonValue } from '../api/config'
import { pathKey, type DiffType } from '../utils/diff'

const props = withDefaults(
  defineProps<{
    value: JsonValue
    name?: string
    path?: string[]
    diff?: Map<string, DiffType>
    depth?: number
    /** Bumped by the parent to force expand/collapse all. */
    expandSignal?: { open: boolean; n: number }
  }>(),
  { name: undefined, path: () => [], diff: undefined, depth: 0, expandSignal: undefined },
)

const isArr = computed(() => Array.isArray(props.value))
const isContainer = computed(() => typeof props.value === 'object' && props.value !== null)
const entries = computed<[string, JsonValue][]>(() => {
  if (Array.isArray(props.value)) return props.value.map((v, i) => [String(i), v])
  if (props.value && typeof props.value === 'object') return Object.entries(props.value)
  return []
})

const key = computed(() => pathKey(props.path))
const status = computed(() => props.diff?.get(key.value))
const hasChangedDescendant = computed(() => {
  if (!props.diff || !isContainer.value || status.value) return false
  const prefix = key.value ? key.value + '/' : ''
  for (const k of props.diff.keys()) if (k.startsWith(prefix) && k !== key.value) return true
  return false
})

const open = ref(props.depth < 2 || hasChangedDescendant.value)
watch(
  () => props.expandSignal,
  (s) => {
    if (s) open.value = s.open
  },
)

const preview = computed(() =>
  isArr.value ? `[${entries.value.length}]` : `{${entries.value.length}}`,
)

const leafClass = computed(() => {
  const v = props.value
  if (v === null) return 'jt-null'
  return 'jt-' + typeof v
})
const leafText = computed(() => (typeof props.value === 'string' ? JSON.stringify(props.value) : String(props.value)))
</script>

<template>
  <div
    class="jt-node"
    :class="[status ? `jt-${status}` : '', hasChangedDescendant ? 'jt-has-diff' : '']"
  >
    <template v-if="isContainer">
      <button type="button" class="jt-row jt-toggle" :aria-expanded="open" @click="open = !open">
        <v-icon size="small" class="jt-chev">{{ open ? 'mdi-chevron-down' : 'mdi-chevron-right' }}</v-icon>
        <span v-if="name !== undefined" class="jt-key">{{ name }}</span>
        <span class="jt-meta">{{ isArr ? '[' : '{' }}{{ open ? '' : ` ${preview} ${isArr ? ']' : '}'}` }}</span>
      </button>
      <div v-show="open" class="jt-children">
        <JsonTree
          v-for="[k, v] in entries"
          :key="k"
          :name="k"
          :value="v"
          :path="[...path, k]"
          :diff="diff"
          :depth="depth + 1"
          :expand-signal="expandSignal"
        />
        <div class="jt-meta jt-close">{{ isArr ? ']' : '}' }}</div>
      </div>
    </template>
    <div v-else class="jt-row">
      <span class="jt-chev-space" />
      <span v-if="name !== undefined" class="jt-key">{{ name }}</span>
      <span :class="leafClass">{{ leafText }}</span>
    </div>
  </div>
</template>

<style scoped>
.jt-node {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.85rem;
  line-height: 1.5;
}
.jt-row {
  display: flex;
  align-items: baseline;
  gap: 0.4rem;
  width: 100%;
  text-align: left;
  background: none;
  border: 0;
  color: inherit;
  font: inherit;
  padding: 0 0.25rem;
  border-radius: 4px;
  overflow-wrap: anywhere;
}
.jt-toggle {
  cursor: pointer;
}
.jt-toggle:hover {
  background: rgba(var(--v-theme-on-surface), 0.06);
}
.jt-chev,
.jt-chev-space {
  flex: none;
  width: 1.25rem;
}
.jt-children {
  margin-left: 0.9rem;
  padding-left: 0.4rem;
  border-left: 1px dashed rgba(var(--v-theme-on-surface), 0.2);
}
.jt-close {
  padding-left: 1.65rem;
}
.jt-key {
  color: rgb(var(--v-theme-neutral));
}
.jt-key::after {
  content: ':';
  color: rgba(var(--v-theme-on-surface), 0.6);
}
.jt-meta {
  color: rgba(var(--v-theme-on-surface), 0.6);
}
.jt-string {
  color: rgb(var(--v-theme-success));
}
.jt-number {
  color: rgb(var(--v-theme-info));
}
.jt-boolean {
  color: rgb(var(--v-theme-warning));
}
.jt-null {
  color: rgba(var(--v-theme-on-surface), 0.55);
  font-style: italic;
}
.jt-changed > .jt-row {
  background: rgba(var(--v-theme-warning), 0.25);
}
.jt-added > .jt-row {
  background: rgba(var(--v-theme-success), 0.25);
}
.jt-removed > .jt-row {
  background: rgba(var(--v-theme-error), 0.25);
}
.jt-has-diff > .jt-row {
  box-shadow: inset 3px 0 0 rgb(var(--v-theme-warning));
}
</style>
