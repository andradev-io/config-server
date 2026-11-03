<script setup lang="ts">
// "Compare": diff of the merged configuration of two environments of the context app.
// The baseline follows the context environment; the target defaults to the next environment.
import { computed, onBeforeUnmount, ref, shallowReactive, watch } from 'vue'
import { ConfigApiError, fetchConfig, type JsonValue } from '../api/config'
import { useContext } from '../composables/useContext'
import EnvChip from '../components/EnvChip.vue'
import EnvDot from '../components/EnvDot.vue'
import JsonTree from '../components/JsonTree.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import { alertTitle, alertType } from '../utils/alerts'
import { diffJson, diffMapForSide } from '../utils/diff'

const props = defineProps<{ active: boolean }>()
const ctx = useContext()

type Slot = 'baseline' | 'target'
interface Panel {
  state: 'idle' | 'loading' | 'ok' | 'error'
  data?: JsonValue
  error?: { kind: string; message: string }
}

const app = computed(() => ctx.state.app)
// Only the environments the caller may read in this application.
const envNames = computed(() => ctx.environmentsFor(app.value).map((e) => e.name))
const baseline = ref<string | null>(null)
const target = ref<string | null>(null)

/** Next environment after `env` in the declared order (or the previous one for the last). */
function neighbour(env: string | null): string | null {
  const list = envNames.value
  if (!env || list.length < 2) return list.find((e) => e !== env) ?? null
  const i = list.indexOf(env)
  return i < 0 ? list[0] : (list[i + 1] ?? list[i - 1] ?? null)
}
function applyDefaults() {
  baseline.value = ctx.state.env ?? envNames.value[0] ?? null
  if (!target.value || target.value === baseline.value || !envNames.value.includes(target.value)) {
    target.value = neighbour(baseline.value)
  }
}
watch(() => ctx.state.env, applyDefaults)
watch(envNames, () => {
  if (!baseline.value || !target.value) applyDefaults()
})
applyDefaults()

function swap() {
  const b = baseline.value
  baseline.value = target.value
  target.value = b
}

const panels = shallowReactive<Record<Slot, Panel>>({ baseline: { state: 'idle' }, target: { state: 'idle' } })
const controllers: Partial<Record<Slot, AbortController>> = {}
async function loadSlot(slot: Slot, env: string) {
  controllers[slot]?.abort()
  const c = (controllers[slot] = new AbortController())
  panels[slot] = { state: 'loading' }
  try {
    const data = await fetchConfig(app.value!, env, { signal: c.signal })
    if (!c.signal.aborted) panels[slot] = { state: 'ok', data }
  } catch (e) {
    if (c.signal.aborted) return
    panels[slot] = {
      state: 'error',
      error: e instanceof ConfigApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' },
    }
  }
}
const canCompare = computed(() => !!app.value && !!baseline.value && !!target.value)
function compare() {
  if (!canCompare.value) {
    panels.baseline = { state: 'idle' }
    panels.target = { state: 'idle' }
    return
  }
  void loadSlot('baseline', baseline.value!)
  void loadSlot('target', target.value!)
}

let stale = true
watch([app, baseline, target], () => {
  if (props.active) compare()
  else stale = true
})
watch(
  () => props.active,
  (a) => {
    if (a && stale) {
      stale = false
      compare()
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  controllers.baseline?.abort()
  controllers.target?.abort()
})

const diffs = computed(() =>
  panels.baseline.state === 'ok' && panels.target.state === 'ok' ? diffJson(panels.baseline.data, panels.target.data) : null,
)
const baselineMap = computed(() => (diffs.value ? diffMapForSide(diffs.value, 'baseline') : undefined))
const targetMap = computed(() => (diffs.value ? diffMapForSide(diffs.value, 'target') : undefined))
const expandSignal = ref({ open: true, n: 0 })
const sideEnv = (slot: Slot) => (slot === 'baseline' ? baseline.value : target.value)

const snackbar = ref({ show: false, text: '' })
async function copy(data: JsonValue | undefined) {
  try {
    await navigator.clipboard.writeText(JSON.stringify(data, null, 2))
    snackbar.value = { show: true, text: 'Copied to clipboard' }
  } catch {
    snackbar.value = { show: true, text: 'Copy failed (clipboard unavailable)' }
  }
}
const diffLabel = { added: 'added', removed: 'removed', changed: 'changed' } as const
const fmt = (v: JsonValue | undefined) => (v === undefined ? '-' : JSON.stringify(v))
</script>

<template>
  <div class="compare-view">
    <PageHeader title="Compare environments" icon="mdi-compare-horizontal">
      <template #subtitle>
        <span v-if="app" class="font-mono">{{ app }}</span>
        <span v-if="app" aria-hidden="true">·</span>
        <span>merged configuration, baseline vs target</span>
      </template>
      <template #actions>
        <v-btn prepend-icon="mdi-refresh" :disabled="!canCompare" @click="compare">Refresh</v-btn>
      </template>
    </PageHeader>

    <NoAppState v-if="!app" message="Choose an application to compare its environments." />
    <template v-else>
      <div class="sides mb-4">
        <v-select
          v-model="baseline"
          :items="envNames"
          label="Baseline environment"
          density="comfortable"
          hide-details
          no-data-text="No environments"
        >
          <template #prepend-inner><EnvDot v-if="baseline" :name="baseline" class="mr-1" /></template>
        </v-select>
        <v-btn icon="mdi-swap-horizontal" variant="text" aria-label="Swap baseline and target" title="Swap" @click="swap" />
        <v-select
          v-model="target"
          :items="envNames"
          label="Target environment"
          density="comfortable"
          hide-details
          no-data-text="No environments"
        >
          <template #prepend-inner><EnvDot v-if="target" :name="target" class="mr-1" /></template>
        </v-select>
      </div>

      <v-alert v-if="diffs" :type="diffs.length ? 'info' : 'success'" variant="tonal" density="compact" class="mb-4">
        {{ diffs.length ? `${diffs.length} difference${diffs.length === 1 ? '' : 's'} found.` : 'No differences.' }}
      </v-alert>

      <v-card v-if="diffs && diffs.length" class="mb-4">
        <div class="table-scroll">
          <v-table density="compact">
            <thead>
              <tr>
                <th>Key</th>
                <th>Type</th>
                <th><span class="d-inline-flex align-center ga-2"><EnvDot :name="baseline ?? ''" />{{ baseline }}</span></th>
                <th><span class="d-inline-flex align-center ga-2"><EnvDot :name="target ?? ''" />{{ target }}</span></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="d in diffs" :key="d.display">
                <td class="font-mono text-break">{{ d.display }}</td>
                <td>
                  <v-chip size="x-small" label :color="d.type === 'added' ? 'success' : d.type === 'removed' ? 'error' : 'warning'">
                    {{ diffLabel[d.type] }}
                  </v-chip>
                </td>
                <td class="font-mono text-break">{{ fmt(d.before) }}</td>
                <td class="font-mono text-break">{{ fmt(d.after) }}</td>
              </tr>
            </tbody>
          </v-table>
        </div>
      </v-card>

      <v-row>
        <v-col v-for="slot in (['baseline', 'target'] as const)" :key="slot" cols="12" md="6">
          <v-card class="h-100">
            <v-card-item>
              <div class="d-flex align-center flex-wrap ga-2">
                <span class="text-overline text-medium-emphasis">{{ slot === 'baseline' ? 'Baseline' : 'Target' }}</span>
                <EnvChip v-if="sideEnv(slot)" :name="sideEnv(slot)!" />
                <v-spacer />
                <v-btn v-if="panels[slot].state === 'ok'" size="small" prepend-icon="mdi-content-copy" @click="copy(panels[slot].data)">
                  Copy
                </v-btn>
              </div>
            </v-card-item>
            <v-card-text>
              <v-progress-linear v-if="panels[slot].state === 'loading'" indeterminate />
              <v-alert
                v-else-if="panels[slot].state === 'error'"
                :type="alertType(panels[slot].error?.kind)"
                :title="alertTitle(panels[slot].error?.kind)"
                :text="panels[slot].error?.message"
                variant="tonal"
              />
              <JsonTree
                v-else-if="panels[slot].data !== undefined"
                :value="panels[slot].data as JsonValue"
                :diff="slot === 'baseline' ? baselineMap : targetMap"
                :expand-signal="expandSignal"
              />
              <div v-else class="text-medium-emphasis">Pick both environments.</div>
            </v-card-text>
          </v-card>
        </v-col>
      </v-row>
    </template>

    <v-snackbar v-model="snackbar.show" timeout="2000">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.compare-view {
  min-width: 0;
}
.sides {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);
  align-items: center;
  gap: 8px;
  max-width: 720px;
}
.table-scroll {
  overflow-x: auto;
}
</style>
