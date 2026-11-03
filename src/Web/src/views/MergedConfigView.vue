<script setup lang="ts">
// "View": merged configuration (default + override) of the context app and environment, read-only.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ConfigApiError, fetchConfigText, getDisplayApiBaseUrl, type ConfigFormat } from '../api/config'
import { useContext } from '../composables/useContext'
import CodeEditor from '../components/CodeEditor.vue'
import EnvChip from '../components/EnvChip.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import { alertTitle, alertType } from '../utils/alerts'

const props = defineProps<{ active: boolean; dark: boolean }>()
const ctx = useContext()

const format = ref<ConfigFormat>('json')
const formats: ConfigFormat[] = ['json', 'yaml', 'env']
const panel = ref<{ state: 'idle' | 'loading' | 'ok' | 'error'; text?: string; error?: { kind: string; message: string } }>({
  state: 'idle',
})
const app = computed(() => ctx.state.app)
const env = computed(() => ctx.state.env)

let ctrl: AbortController | undefined
async function load() {
  ctrl?.abort()
  if (!app.value || !env.value) {
    panel.value = { state: 'idle' }
    return
  }
  const c = (ctrl = new AbortController())
  panel.value = { state: 'loading', text: panel.value.text }
  try {
    const text = await fetchConfigText(app.value, env.value, format.value, { signal: c.signal })
    if (!c.signal.aborted) panel.value = { state: 'ok', text }
  } catch (e) {
    if (c.signal.aborted) return
    panel.value = {
      state: 'error',
      error: e instanceof ConfigApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' },
    }
  }
}

// Loads when shown and whenever app / environment / format change while visible.
let stale = true
watch([app, env, format], () => {
  if (props.active) void load()
  else stale = true
})
watch(
  () => props.active,
  (a) => {
    if (a && stale) {
      stale = false
      void load()
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => ctrl?.abort())

const snackbar = ref({ show: false, text: '' })
async function copy() {
  try {
    await navigator.clipboard.writeText(panel.value.text ?? '')
    snackbar.value = { show: true, text: 'Copied to clipboard' }
  } catch {
    snackbar.value = { show: true, text: 'Copy failed (clipboard unavailable)' }
  }
}
const apiBase = getDisplayApiBaseUrl()
</script>

<template>
  <div class="merged-view">
    <PageHeader title="Merged configuration" icon="mdi-file-eye-outline">
      <template #subtitle>
        <template v-if="app">
          <span class="font-mono">{{ app }}</span>
          <EnvChip v-if="env" :name="env" size="x-small" />
          <span aria-hidden="true">·</span>
        </template>
        <span>default + environment override, as clients receive it</span>
      </template>
      <template #actions>
        <v-btn-toggle v-model="format" mandatory density="compact" variant="tonal" divided aria-label="Format">
          <v-btn v-for="f in formats" :key="f" :value="f" size="small">{{ f }}</v-btn>
        </v-btn-toggle>
        <v-btn prepend-icon="mdi-content-copy" :disabled="panel.state !== 'ok'" @click="copy">Copy</v-btn>
        <v-btn prepend-icon="mdi-refresh" :loading="panel.state === 'loading'" :disabled="!app || !env" @click="load">Refresh</v-btn>
      </template>
    </PageHeader>

    <NoAppState v-if="!app" message="Choose an application to see its merged configuration." />
    <v-card v-else-if="!env" class="pa-6 text-center text-medium-emphasis">Choose an environment in the top bar.</v-card>
    <template v-else>
      <v-progress-linear v-if="panel.state === 'loading'" indeterminate class="mb-2" />
      <v-alert
        v-if="panel.state === 'error'"
        :type="alertType(panel.error?.kind)"
        :title="alertTitle(panel.error?.kind)"
        :text="panel.error?.message"
        variant="tonal"
      />
      <v-card v-else-if="panel.text !== undefined" class="pa-2">
        <CodeEditor :model-value="panel.text" :language="format" :dark="dark" read-only :lint="false" class="view-editor" />
      </v-card>
      <div class="text-caption text-medium-emphasis mt-2">API: {{ apiBase }}</div>
    </template>

    <v-snackbar v-model="snackbar.show" timeout="2000">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.merged-view {
  min-width: 0;
}
.view-editor {
  height: calc(100vh - 260px);
  min-height: 320px;
}
</style>
