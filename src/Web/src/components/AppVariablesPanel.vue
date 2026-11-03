<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { FilesApiError } from '../api/files'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import { variablesWriteDenial } from '../utils/access'
import PermissionTip from './PermissionTip.vue'
import {
  getAppStatus,
  getAppVariables,
  putAppVariables,
  VARIABLE_TYPES,
  type AppStatus,
} from '../api/variables'
import {
  fromRows,
  newRow,
  statusLabel,
  toRows,
  validateRows,
  type VariableRow,
} from '../utils/variables'

const props = defineProps<{ app: string | null; refreshToken: number }>()
// The declarations apply to the whole application: without write on all its environments they are read-only.
const { grants } = useAuth()
const writeDenial = computed(() => (props.app ? variablesWriteDenial(grants.value, props.app) : null))
const canWrite = computed(() => !!props.app && writeDenial.value === null)

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')

const open = ref<number[]>([])
const rows = ref<VariableRow[]>([])
const original = ref('[]')
const loading = ref(false)
const loadError = ref<ViewError | null>(null)
const saving = ref(false)
const saveError = ref<ViewError | null>(null)
const savedNote = ref(false)

const snapshot = (r: VariableRow[]) => JSON.stringify(fromRows(r))
const dirty = computed(() => snapshot(rows.value) !== original.value)
const errors = computed(() => validateRows(rows.value))
const canSave = computed(() => canWrite.value && dirty.value && errors.value.size === 0 && !saving.value)
// Unsaved declarations are lost when the context app changes: the app bar asks first.
onBeforeUnmount(useContext().registerUnsaved('Editor > Variables', () => dirty.value))

let varsCtrl: AbortController | undefined
async function loadVariables() {
  varsCtrl?.abort()
  rows.value = []
  original.value = '[]'
  saveError.value = null
  if (!props.app) return
  const ctrl = (varsCtrl = new AbortController())
  loading.value = true
  loadError.value = null
  try {
    const vars = await getAppVariables(props.app, { signal: ctrl.signal })
    if (ctrl.signal.aborted) return
    rows.value = toRows(vars)
    original.value = snapshot(rows.value)
  } catch (e) {
    if (!ctrl.signal.aborted) loadError.value = toError(e)
  } finally {
    if (!ctrl.signal.aborted) loading.value = false
  }
}

// ---- status
const status = ref<AppStatus | null>(null)
const statusLoading = ref(false)
const statusError = ref<ViewError | null>(null)
const expanded = ref<string[]>([])
let statusCtrl: AbortController | undefined
async function loadStatus() {
  statusCtrl?.abort()
  status.value = null
  if (!props.app) return
  const ctrl = (statusCtrl = new AbortController())
  statusLoading.value = true
  statusError.value = null
  try {
    const s = await getAppStatus(props.app, { signal: ctrl.signal })
    if (!ctrl.signal.aborted) status.value = s
  } catch (e) {
    if (!ctrl.signal.aborted) statusError.value = toError(e)
  } finally {
    if (!ctrl.signal.aborted) statusLoading.value = false
  }
}

watch(
  () => props.app,
  () => {
    expanded.value = []
    void loadVariables()
    void loadStatus()
  },
  { immediate: true },
)
// A config file was saved elsewhere: only the status needs refreshing.
watch(
  () => props.refreshToken,
  () => void loadStatus(),
)

async function save() {
  if (!props.app || !canSave.value) return
  saving.value = true
  saveError.value = null
  try {
    await putAppVariables(props.app, fromRows(rows.value))
    original.value = snapshot(rows.value)
    savedNote.value = true
    setTimeout(() => (savedNote.value = false), 2500)
    void loadStatus()
  } catch (e) {
    saveError.value = toError(e)
  } finally {
    saving.value = false
  }
}

function addRow(key = '') {
  rows.value.push(newRow(key))
}
function removeRow(id: number) {
  rows.value = rows.value.filter((r) => r.id !== id)
}
function declare(key: string) {
  if (!rows.value.some((r) => r.key === key)) addRow(key)
  if (status.value) status.value = { ...status.value, undeclared: status.value.undeclared.filter((k) => k !== key) }
}
function declareAll() {
  for (const k of [...(status.value?.undeclared ?? [])]) declare(k)
}

const envItems = computed(() => {
  const set = new Set<string>(status.value?.environments.map((e) => e.name) ?? [])
  for (const r of rows.value) for (const e of r.environments) set.add(e)
  return [...set].sort()
})
const typeItems: string[] = [...VARIABLE_TYPES]

function toggleExpanded(name: string) {
  expanded.value = expanded.value.includes(name)
    ? expanded.value.filter((n) => n !== name)
    : [...expanded.value, name]
}
const hasDetails = (e: AppStatus['environments'][number]) =>
  !!e.error || e.missing.length > 0 || e.typeMismatches.length > 0 || e.secretsInPlainText.length > 0
const chipColor = (e: AppStatus['environments'][number]) => (e.error ? 'error' : e.ok ? 'success' : 'warning')
const chipIcon = (e: AppStatus['environments'][number]) =>
  e.error ? 'mdi-alert-circle-outline' : e.ok ? 'mdi-check-circle-outline' : 'mdi-alert-outline'
</script>

<template>
  <v-expansion-panels v-model="open" class="mb-3">
    <v-expansion-panel :value="0" :disabled="!app">
      <v-expansion-panel-title class="text-subtitle-1">
        <v-icon start>mdi-variable</v-icon>
        Variables
        <v-chip v-if="dirty" size="x-small" color="warning" label class="ml-2">unsaved</v-chip>
        <v-chip v-else-if="rows.length" size="x-small" label class="ml-2">{{ rows.length }}</v-chip>
        <v-chip
          v-if="status && status.undeclared.length"
          size="x-small"
          color="warning"
          label
          class="ml-2"
          prepend-icon="mdi-help-circle-outline"
        >
          {{ status.undeclared.length }} undeclared
        </v-chip>
      </v-expansion-panel-title>
      <v-expansion-panel-text>
        <!-- Status matrix -->
        <div class="d-flex align-center ga-2 mb-2">
          <span class="text-subtitle-2">Status</span>
          <v-spacer />
          <v-btn
            size="x-small"
            icon="mdi-refresh"
            aria-label="Refresh status"
            :loading="statusLoading"
            @click="loadStatus"
          />
        </div>
        <v-progress-linear v-if="statusLoading && !status" indeterminate class="mb-2" />
        <v-alert v-if="statusError" :type="alertType(statusError.kind)" variant="tonal" density="compact" class="mb-2">
          {{ statusError.message }}
        </v-alert>
        <template v-if="status">
          <div v-if="!status.environments.length" class="text-medium-emphasis mb-2">
            No environments to check for this application.
          </div>
          <div class="d-flex flex-wrap ga-2 mb-2">
            <v-chip
              v-for="e in status.environments"
              :key="e.name"
              :color="chipColor(e)"
              :prepend-icon="chipIcon(e)"
              :append-icon="hasDetails(e) ? (expanded.includes(e.name) ? 'mdi-chevron-up' : 'mdi-chevron-down') : undefined"
              :aria-expanded="hasDetails(e) ? expanded.includes(e.name) : undefined"
              :link="hasDetails(e)"
              @click="hasDetails(e) && toggleExpanded(e.name)"
            >
              {{ e.name }}: {{ statusLabel(e) }}
            </v-chip>
          </div>
          <template v-for="e in status.environments" :key="'d-' + e.name">
            <v-sheet v-if="expanded.includes(e.name) && hasDetails(e)" color="surface-light" rounded="lg" class="pa-3 mb-2 status-detail">
              <div class="text-subtitle-2 mb-1">{{ e.name }}</div>
              <v-alert v-if="e.error" type="error" variant="tonal" density="compact" class="mb-2">{{ e.error }}</v-alert>
              <div v-if="e.missing.length" class="mb-2">
                <div class="text-caption text-medium-emphasis">Missing</div>
                <v-chip v-for="k in e.missing" :key="k" size="small" color="error" label class="mr-1 mb-1 font-mono">
                  {{ k }}
                </v-chip>
              </div>
              <div v-if="e.typeMismatches.length" class="mb-2">
                <div class="text-caption text-medium-emphasis">Type mismatches</div>
                <div v-for="m in e.typeMismatches" :key="m.key" class="font-mono text-body-2 text-break">
                  {{ m.key }}: expected {{ m.expected }}, got {{ m.actual }}
                </div>
              </div>
              <div v-if="e.secretsInPlainText.length">
                <div class="text-caption text-medium-emphasis">Secrets in plain text</div>
                <v-chip
                  v-for="k in e.secretsInPlainText"
                  :key="k"
                  size="small"
                  color="warning"
                  label
                  prepend-icon="mdi-lock-open-alert-outline"
                  class="mr-1 mb-1 font-mono"
                >
                  {{ k }}
                </v-chip>
              </div>
            </v-sheet>
          </template>
          <v-sheet v-if="status.undeclared.length" color="surface-light" rounded="lg" class="pa-3 mb-2">
            <div class="d-flex align-center ga-2 mb-1">
              <span class="text-subtitle-2">Undeclared keys</span>
              <v-spacer />
              <PermissionTip :reason="writeDenial">
                <v-btn size="x-small" prepend-icon="mdi-plus" :disabled="!canWrite" @click="declareAll">Declare all</v-btn>
              </PermissionTip>
            </div>
            <div v-for="k in status.undeclared" :key="k" class="d-flex align-center ga-2">
              <span class="font-mono text-body-2 text-break flex-grow-1">{{ k }}</span>
              <PermissionTip :reason="writeDenial">
                <v-btn size="x-small" prepend-icon="mdi-plus" :disabled="!canWrite" @click="declare(k)">Declare</v-btn>
              </PermissionTip>
            </div>
          </v-sheet>
        </template>

        <v-divider class="my-3" />

        <!-- Declared variables -->
        <div class="text-subtitle-2 mb-2">Declared variables</div>
        <v-progress-linear v-if="loading" indeterminate class="mb-2" />
        <v-alert v-if="loadError" :type="alertType(loadError.kind)" variant="tonal" density="compact" class="mb-2">
          <div class="d-flex align-center ga-2">
            <span>{{ loadError.message }}</span>
            <v-spacer />
            <v-btn size="x-small" @click="loadVariables">Retry</v-btn>
          </div>
        </v-alert>
        <template v-else-if="!loading">
          <v-alert
            v-if="saveError"
            :type="alertType(saveError.kind)"
            :title="saveError.kind === 'bad-request' ? 'Validation error' : undefined"
            variant="tonal"
            density="compact"
            class="mb-2"
            closable
            @click:close="saveError = null"
          >
            {{ saveError.message }}
          </v-alert>
          <div v-if="!rows.length" class="text-medium-emphasis mb-2">No variables declared for this application.</div>
          <div v-else class="vars-scroll">
            <table class="vars-table">
              <thead>
                <tr>
                  <th>Key</th>
                  <th>Type</th>
                  <th>Required</th>
                  <th>Secret</th>
                  <th>Environments</th>
                  <th>Description</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                <tr v-for="r in rows" :key="r.id">
                  <td class="col-key">
                    <v-text-field
                      v-model="r.key"
                      aria-label="Key"
                      :readonly="!canWrite"
                      density="compact"
                      hide-details="auto"
                      autocomplete="off"
                      :error-messages="errors.get(r.id)?.key"
                    />
                  </td>
                  <td class="col-type">
                    <v-select
                      v-model="r.type"
                      :items="typeItems"
                      aria-label="Type"
                      :readonly="!canWrite"
                      density="compact"
                      hide-details="auto"
                      :error-messages="errors.get(r.id)?.type"
                    />
                  </td>
                  <td><v-switch v-model="r.required" aria-label="Required" density="compact" hide-details color="primary" :readonly="!canWrite" /></td>
                  <td><v-switch v-model="r.secret" aria-label="Secret" density="compact" hide-details color="primary" :readonly="!canWrite" /></td>
                  <td class="col-env">
                    <v-select
                      v-model="r.environments"
                      :items="envItems"
                      :readonly="!canWrite"
                      aria-label="Environments (empty = all)"
                      placeholder="All"
                      persistent-placeholder
                      multiple
                      chips
                      :closable-chips="canWrite"
                      density="compact"
                      hide-details
                    />
                  </td>
                  <td class="col-desc">
                    <v-text-field
                      v-model="r.description"
                      aria-label="Description"
                      density="compact"
                      hide-details
                      autocomplete="off"
                      :readonly="!canWrite"
                    />
                  </td>
                  <td>
                    <v-btn
                      icon="mdi-delete-outline"
                      size="small"
                      variant="text"
                      color="error"
                      aria-label="Remove variable"
                      :disabled="!canWrite"
                      @click="removeRow(r.id)"
                    />
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="d-flex flex-wrap align-center ga-2 mt-2">
            <PermissionTip :reason="writeDenial">
              <v-btn size="small" prepend-icon="mdi-plus" :disabled="!canWrite" @click="addRow()">Add variable</v-btn>
            </PermissionTip>
            <PermissionTip :reason="writeDenial">
              <v-btn
                size="small"
                color="primary"
                variant="flat"
                prepend-icon="mdi-content-save-outline"
                :loading="saving"
                :disabled="!canSave"
                @click="save"
              >
                Save variables
              </v-btn>
            </PermissionTip>
            <v-btn size="small" prepend-icon="mdi-undo" :disabled="!dirty || saving" @click="loadVariables">Revert</v-btn>
            <span v-if="savedNote" class="text-success text-caption">Saved</span>
            <span v-else-if="dirty && errors.size" class="text-error text-caption">Fix the highlighted rows to save.</span>
          </div>
        </template>
      </v-expansion-panel-text>
    </v-expansion-panel>
  </v-expansion-panels>
</template>

<style scoped>
.vars-scroll {
  overflow-x: auto;
}
.vars-table {
  border-collapse: collapse;
  min-width: 760px;
  width: 100%;
}
.vars-table th {
  text-align: left;
  font-weight: 500;
  padding: 2px 4px;
  white-space: nowrap;
}
.vars-table td {
  padding: 4px;
  vertical-align: top;
}
.col-key {
  min-width: 160px;
}
.col-type {
  min-width: 120px;
}
.col-env {
  min-width: 170px;
}
.col-desc {
  min-width: 160px;
}
.status-detail {
  min-width: 0;
}
</style>
