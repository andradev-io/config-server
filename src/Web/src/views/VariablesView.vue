<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useDisplay } from 'vuetify'
import { FilesApiError } from '../api/files'
import { getVariable, searchVariables, type IndexWarning, type VariableEntry } from '../api/variables'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import EnvDot from '../components/EnvDot.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import { hasNoAccess } from '../utils/access'
import { buildMatrix, CELL_VISUAL, USAGE_VISUAL, usageKind, variablesToCsv } from '../utils/variables'

const { smAndDown } = useDisplay()
const { grants } = useAuth()
const noAccess = computed(() => hasNoAccess(grants.value))

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')

// The application / environment filters start from the global context and follow it; clear them to
// search across all applications or environments.
const ctx = useContext()
const search = ref('')
const appFilter = ref<string | null>(ctx.state.app)
const envFilter = ref<string | null>(ctx.state.env)
const declaredOnly = ref(false)
const apps = computed(() => ctx.state.apps)
const envNames = computed(() => ctx.state.environments.map((e) => e.name))
watch(
  () => ctx.state.app,
  (a) => (appFilter.value = a),
)
watch(
  () => ctx.state.env,
  (e) => (envFilter.value = e),
)

const items = ref<VariableEntry[]>([])
const warnings = ref<IndexWarning[]>([])
const loading = ref(false)
const error = ref<ViewError | null>(null)
const loaded = ref(false)
const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

let ctrl: AbortController | undefined
async function load() {
  ctrl?.abort()
  const c = (ctrl = new AbortController())
  loading.value = true
  error.value = null
  try {
    const r = await searchVariables(
      {
        search: search.value.trim(),
        app: appFilter.value ?? undefined,
        env: envFilter.value ?? undefined,
        declaredOnly: declaredOnly.value,
      },
      { signal: c.signal },
    )
    if (c.signal.aborted) return
    items.value = r.variables
    warnings.value = r.warnings
    loaded.value = true
  } catch (e) {
    if (c.signal.aborted) return
    error.value = toError(e)
    items.value = []
    warnings.value = []
  } finally {
    if (!c.signal.aborted) loading.value = false
  }
}

// Debounced search; the other filters apply immediately.
let timer: ReturnType<typeof setTimeout> | undefined
watch(search, () => {
  clearTimeout(timer)
  timer = setTimeout(() => void load(), 300)
})
watch([appFilter, envFilter, declaredOnly], () => {
  clearTimeout(timer)
  void load()
})
onBeforeUnmount(() => {
  clearTimeout(timer)
  ctrl?.abort()
  detailCtrl?.abort()
})
onMounted(() => void load())

const headers = [
  { title: 'Key', key: 'key' },
  { title: 'Type', key: 'type', width: 110 },
  { title: 'Apps', key: 'appCount', width: 90, align: 'end' as const },
  { title: 'Used by', key: 'usedBy', sortable: false },
]

const legend = Object.values(USAGE_VISUAL)

// ---- detail
const detailOpen = ref(false)
const detailKey = ref('')
const detail = ref<VariableEntry | null>(null)
const detailLoading = ref(false)
const detailError = ref<ViewError | null>(null)
let detailCtrl: AbortController | undefined

async function openDetail(key: string) {
  detailKey.value = key
  detailOpen.value = true
  detail.value = null
  detailError.value = null
  detailCtrl?.abort()
  const c = (detailCtrl = new AbortController())
  detailLoading.value = true
  try {
    const v = await getVariable(key, { signal: c.signal })
    if (!c.signal.aborted) detail.value = v
  } catch (e) {
    if (!c.signal.aborted) detailError.value = toError(e)
  } finally {
    if (!c.signal.aborted) detailLoading.value = false
  }
}
const matrix = computed(() => (detail.value ? buildMatrix(detail.value.usedBy, envNames.value) : null))

async function copyKey(key: string) {
  try {
    await navigator.clipboard.writeText(key)
    notify('Key copied')
  } catch {
    notify('Copy failed (clipboard unavailable)')
  }
}

function exportCsv() {
  const blob = new Blob([variablesToCsv(items.value)], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = 'variables.csv'
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <div class="variables-view">
    <PageHeader
      title="Variables"
      icon="mdi-variable"
      subtitle="Cross-project index of required variables. Values are never shown."
    >
      <template #actions>
        <v-btn prepend-icon="mdi-refresh" :loading="loading" @click="load">Refresh</v-btn>
        <v-btn prepend-icon="mdi-file-delimited-outline" :disabled="!items.length" @click="exportCsv">Export CSV</v-btn>
      </template>
    </PageHeader>

    <!-- Signed in without any grant: the empty state instead of errors -->
    <NoAppState v-if="noAccess" />
    <template v-else>
      <div class="filters">
        <div class="filters__search">
          <v-text-field
            v-model="search"
            label="Search keys"
            prepend-inner-icon="mdi-magnify"
            clearable
            density="comfortable"
            hide-details
            autocomplete="off"
          />
        </div>
        <div class="filters__select">
          <v-select
            v-model="appFilter"
            :items="apps"
            label="Application"
            placeholder="All applications"
            persistent-placeholder
            clearable
            density="comfortable"
            hide-details
          />
        </div>
        <div class="filters__select">
          <v-select
            v-model="envFilter"
            :items="envNames"
            label="Environment"
            placeholder="All environments"
            persistent-placeholder
            clearable
            density="comfortable"
            hide-details
          >
            <template #prepend-inner><EnvDot v-if="envFilter" :name="envFilter" class="mr-1" /></template>
          </v-select>
        </div>
        <v-switch v-model="declaredOnly" label="Declared only" density="compact" hide-details color="primary" class="flex-grow-0 text-no-wrap" />
      </div>

      <div class="d-flex flex-wrap ga-3 my-3 text-caption" aria-label="Legend">
        <span v-for="l in legend" :key="l.label" class="d-inline-flex align-center ga-1">
          <v-icon :icon="l.icon" :color="l.color" size="small" />{{ l.label }}
        </span>
      </div>

      <v-alert v-if="error" :type="alertType(error.kind)" variant="tonal" class="mb-3">
        <div class="d-flex align-center ga-2">
          <span>{{ error.message }}</span>
          <v-spacer />
          <v-btn size="small" @click="load">Retry</v-btn>
        </div>
      </v-alert>

      <v-expansion-panels v-if="warnings.length" class="mb-3">
        <v-expansion-panel>
          <v-expansion-panel-title class="text-warning">
            <v-icon start color="warning">mdi-alert-outline</v-icon>
            {{ warnings.length }} warning{{ warnings.length === 1 ? '' : 's' }} while indexing
          </v-expansion-panel-title>
          <v-expansion-panel-text>
            <div v-for="(w, i) in warnings" :key="i" class="text-body-2 mb-1 text-break">
              <span class="font-mono">{{ w.app }}/{{ w.file }}</span>: {{ w.error }}
            </div>
          </v-expansion-panel-text>
        </v-expansion-panel>
      </v-expansion-panels>

      <v-progress-linear v-if="loading" indeterminate class="mb-2" />

      <div v-if="loaded && !loading && !error && !items.length" class="text-medium-emphasis pa-4 text-center">
        No variables match the current filters.
      </div>

      <v-data-table
        v-if="items.length"
        :headers="headers"
        :items="items"
        item-value="key"
        density="comfortable"
        class="rounded-lg"
        hover
        :items-per-page="25"
        @click:row="(_: Event, { item }: { item: VariableEntry }) => openDetail(item.key)"
      >
        <template #item.key="{ item }">
          <span class="font-mono text-break">{{ item.key }}</span>
        </template>
        <template #item.usedBy="{ item }">
          <div class="d-flex flex-wrap ga-1 py-1">
            <v-chip
              v-for="u in item.usedBy"
              :key="u.app"
              size="small"
              label
              :color="USAGE_VISUAL[usageKind(u)].color"
              :prepend-icon="USAGE_VISUAL[usageKind(u)].icon"
              :title="USAGE_VISUAL[usageKind(u)].label"
            >
              {{ u.app }}
            </v-chip>
          </div>
        </template>
      </v-data-table>
    </template>

    <!-- Usage -->
    <v-dialog v-model="detailOpen" :fullscreen="smAndDown" max-width="860" scrollable>
      <v-card>
        <v-card-title class="d-flex align-center ga-2">
          <span class="text-subtitle-1 text-break">Usage of <span class="font-mono">{{ detailKey }}</span></span>
          <v-spacer />
          <v-btn size="small" prepend-icon="mdi-content-copy" @click="copyKey(detailKey)">Copy key</v-btn>
          <v-btn icon="mdi-close" size="small" variant="text" aria-label="Close" @click="detailOpen = false" />
        </v-card-title>
        <v-card-text>
          <v-progress-linear v-if="detailLoading" indeterminate />
          <v-alert v-else-if="detailError" :type="alertType(detailError.kind)" variant="tonal">
            <div class="d-flex align-center ga-2">
              <span>{{ detailError.message }}</span>
              <v-spacer />
              <v-btn size="small" @click="openDetail(detailKey)">Retry</v-btn>
            </div>
          </v-alert>
          <template v-else-if="detail">
            <div class="text-caption text-medium-emphasis mb-2">
              Type {{ detail.type }} - {{ detail.appCount }} application{{ detail.appCount === 1 ? '' : 's' }}
            </div>
            <div v-if="!detail.usedBy.length" class="text-medium-emphasis">No application uses this variable.</div>
            <v-card v-for="u in detail.usedBy" :key="u.app" color="surface-light" class="mb-3">
              <v-card-title class="text-subtitle-2 d-flex flex-wrap align-center ga-2">
                <span>{{ u.app }}</span>
                <v-chip size="x-small" label :color="u.required ? 'neutral' : 'muted'" :prepend-icon="u.required ? 'mdi-asterisk' : 'mdi-circle-outline'">
                  {{ u.required ? 'required' : 'optional' }}
                </v-chip>
                <v-chip size="x-small" label :color="u.secret ? 'info' : undefined" :prepend-icon="u.secret ? 'mdi-lock-outline' : 'mdi-lock-open-variant-outline'">
                  {{ u.secret ? 'secret' : 'not secret' }}
                </v-chip>
                <v-chip
                  size="x-small"
                  label
                  :color="u.declared ? 'success' : 'warning'"
                  :prepend-icon="u.declared ? 'mdi-check' : 'mdi-help-circle-outline'"
                >
                  {{ u.declared ? 'declared' : 'undeclared' }}
                </v-chip>
              </v-card-title>
              <v-card-text>
                <div class="text-caption text-medium-emphasis">Defined in</div>
                <div class="d-flex flex-wrap ga-1 mb-1">
                  <v-chip v-for="f in u.definedIn" :key="f" size="small" label class="font-mono">{{ f }}</v-chip>
                  <span v-if="!u.definedIn.length" class="text-body-2 text-medium-emphasis">no file</span>
                </div>
              </v-card-text>
            </v-card>

            <template v-if="matrix && matrix.envs.length">
              <div class="text-subtitle-2 mb-1">Environments</div>
              <div class="matrix-scroll">
                <v-table density="compact" class="rounded-lg">
                  <thead>
                    <tr>
                      <th>Application</th>
                      <th v-for="env in matrix.envs" :key="env" class="text-center text-no-wrap">
                        <span class="d-inline-flex align-center ga-1"><EnvDot :name="env" />{{ env }}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="row in matrix.rows" :key="row.app">
                      <td>{{ row.app }}</td>
                      <td v-for="c in row.cells" :key="c.env" class="text-center">
                        <v-icon
                          v-if="c.state"
                          :icon="CELL_VISUAL[c.state].icon"
                          :color="CELL_VISUAL[c.state].color"
                          :aria-label="`${row.app} ${c.env}: ${CELL_VISUAL[c.state].label}`"
                          :title="CELL_VISUAL[c.state].label"
                          size="small"
                        />
                        <span v-else class="text-medium-emphasis" aria-label="not applicable">-</span>
                      </td>
                    </tr>
                  </tbody>
                </v-table>
              </div>
              <div class="d-flex flex-wrap ga-3 mt-2 text-caption">
                <span v-for="(v, k) in CELL_VISUAL" :key="k" class="d-inline-flex align-center ga-1">
                  <v-icon :icon="v.icon" :color="v.color" size="small" />{{ v.label }}
                </span>
              </div>
            </template>
          </template>
        </v-card-text>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" timeout="2000">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.variables-view {
  min-width: 0;
}
.filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
}
.filters__search {
  flex: 1 1 260px;
  max-width: 420px;
}
.filters__select {
  flex: 1 1 200px;
  max-width: 260px;
}
.matrix-scroll {
  overflow-x: auto;
}
:deep(.v-data-table tbody tr) {
  cursor: pointer;
}
</style>
