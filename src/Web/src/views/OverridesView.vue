<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useDisplay } from 'vuetify'
import { FilesApiError } from '../api/files'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import {
  getOverrides,
  putOverrides,
  type OverridesDocument,
  type OverridesPutResult,
  type OverrideVariable,
} from '../api/overrides'
import { VARIABLE_TYPES, type VariableType } from '../api/variables'
import CodeEditor from '../components/CodeEditor.vue'
import EnvDot from '../components/EnvDot.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import PermissionTip from '../components/PermissionTip.vue'
import { addOverrideDenial, scopeWriteDenial, writableScopes } from '../utils/access'
import { envColorCss } from '../utils/envColor'
import {
  buildChanges,
  cellMismatch,
  cellView,
  clearPending,
  emptyPending,
  filterPending,
  filterRows,
  formatValue,
  getPending,
  inferType,
  isScalarType,
  MASK,
  newLocalRow,
  parseValue,
  reconcilePending,
  removePending,
  setPending,
  validateNewKey,
  withLocalRows,
  type PendingSet,
} from '../utils/overrides'

defineProps<{ dark?: boolean }>()
const emit = defineEmits<{ dirty: [boolean]; 'open-editor': [] }>()
const { smAndDown } = useDisplay()
// The application comes from the global context (app bar).
const ctx = useContext()
// Each scope column is editable only with write on it: `default` needs write on every environment of the
// app, an environment column write on that environment. Other columns are read-only.
const { grants } = useAuth()
const scopeDenial = (scope: string): string | null =>
  ctx.state.app ? scopeWriteDenial(grants.value, ctx.state.app, scope) : 'No application selected.'
const canWriteScope = (scope: string) => scopeDenial(scope) === null

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')

// ---- state
const app = computed(() => ctx.state.app)
const doc = ref<OverridesDocument | null>(null)
const loading = ref(false)
const error = ref<ViewError | null>(null)
const pending = ref<PendingSet>(emptyPending())
const added = ref<OverrideVariable[]>([])
const search = ref('')
const onlyOverridden = ref(false)
const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

/** Only the changes the caller may write count (and are saved). */
const allowedPending = computed(() => filterPending(pending.value, canWriteScope))
const dirty = computed(() => allowedPending.value.size > 0)
watch(dirty, (d) => emit('dirty', d), { immediate: true })
defineExpose({ dirty })

const scopes = computed(() => {
  const s = doc.value?.scopes ?? []
  return ['default', ...s.filter((x) => x !== 'default')]
})
const writable = computed(() => (app.value ? writableScopes(grants.value, app.value, scopes.value) : []))
const addDenial = computed(() => (app.value && doc.value ? addOverrideDenial(grants.value, app.value, scopes.value) : null))
// Grants changed (e.g. another user signed in): drop the changes that can no longer be saved.
watch(
  () => JSON.stringify(grants.value),
  () => {
    if (allowedPending.value.size !== pending.value.size) pending.value = allowedPending.value
    if (editing.value && !canWriteScope(editing.value.scope)) cancelEdit()
  },
)
const allRows = computed(() => withLocalRows(doc.value?.variables ?? [], added.value))
const rows = computed(() =>
  filterRows(allRows.value, { search: search.value, onlyOverridden: onlyOverridden.value }, pending.value),
)

// ---- loading
let ctrl: AbortController | undefined
async function load(keepPending = false) {
  if (!app.value) return
  ctrl?.abort()
  const c = (ctrl = new AbortController())
  loading.value = true
  error.value = null
  try {
    const d = await getOverrides(app.value, { signal: c.signal })
    if (c.signal.aborted) return
    doc.value = d
    added.value = added.value.filter((a) => !d.variables.some((v) => v.key === a.key))
    pending.value = keepPending ? reconcilePending(pending.value, d.variables.concat(added.value)) : emptyPending()
    if (!keepPending) added.value = []
  } catch (e) {
    if (c.signal.aborted) return
    error.value = toError(e)
    doc.value = null
  } finally {
    if (!c.signal.aborted) loading.value = false
  }
}

const retry = () => void load(true)
onMounted(() => void load())

// ---- unsaved changes guard
const guard = ref<{ show: boolean; then: () => void }>({ show: false, then: () => {} })
function confirmDiscard(then: () => void) {
  if (!dirty.value) return then()
  guard.value = { show: true, then }
}
function guardConfirm() {
  guard.value.show = false
  guard.value.then()
}
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (dirty.value) {
    e.preventDefault()
    e.returnValue = ''
  }
}
window.addEventListener('beforeunload', onBeforeUnload)
// The app bar asks before switching the application while there are pending changes.
const unregister = ctx.registerUnsaved('Overrides', () => dirty.value)
onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  ctrl?.abort()
  unregister()
})

const reload = () => confirmDiscard(() => void load())

// ---- inline editing
const editing = ref<{ key: string; scope: string } | null>(null)
const editText = ref('')
const editError = ref('')
const isEditing = (k: string, s: string) => editing.value?.key === k && editing.value.scope === s

function currentText(row: OverrideVariable, scope: string): string {
  const v = cellView(row, scope, getPending(pending.value, row.key, scope))
  if (v.kind === 'override' && !v.masked) return formatValue(v.value)
  return ''
}

function startEdit(row: OverrideVariable, scope: string) {
  if (!canWriteScope(scope)) return
  if (!isScalarType(row.type)) return openJson(row, scope)
  editing.value = { key: row.key, scope }
  editText.value = currentText(row, scope)
  editError.value = ''
  void nextTick(() => document.querySelector<HTMLInputElement>('.ov-input input')?.focus())
}
function cancelEdit() {
  editing.value = null
  editError.value = ''
}
/** Returns true when the cell was committed (or unchanged). */
function commitEdit(row: OverrideVariable): boolean {
  const e = editing.value
  if (!e) return true
  const r = parseValue(row.type, editText.value)
  if (!r.ok) {
    editError.value = r.error
    return false
  }
  pending.value = setPending(pending.value, row, e.scope, r.value)
  cancelEdit()
  return true
}
function tabNext(row: OverrideVariable, ev: KeyboardEvent) {
  const e = editing.value
  if (!e) return
  // Next / previous writable column (read-only columns are skipped).
  const i = writable.value.indexOf(e.scope)
  const next = i < 0 ? undefined : writable.value[i + (ev.shiftKey ? -1 : 1)]
  ev.preventDefault()
  if (!commitEdit(row)) return
  if (next) startEdit(row, next)
}
const clearCell = (row: OverrideVariable, scope: string) => (pending.value = removePending(pending.value, row, scope))
const undoCell = (row: OverrideVariable, scope: string) => (pending.value = clearPending(pending.value, row.key, scope))
const hasOverride = (row: OverrideVariable, scope: string) => {
  const c = getPending(pending.value, row.key, scope)
  return c ? !c.remove : !!row.values[scope]
}

// ---- JSON dialog (arrays/objects)
const json = ref<{ show: boolean; row: OverrideVariable | null; scope: string; text: string; error: string }>({
  show: false,
  row: null,
  scope: '',
  text: '',
  error: '',
})
function openJson(row: OverrideVariable, scope: string) {
  const v = cellView(row, scope, getPending(pending.value, row.key, scope))
  const text = v.kind === 'override' && !v.masked ? formatValue(v.value, true) : row.secret ? '' : row.type === 'array' ? '[]' : '{}'
  json.value = { show: true, row, scope, text, error: '' }
}
function applyJson() {
  const j = json.value
  if (!j.row) return
  const r = parseValue(j.row.type, j.text)
  if (!r.ok) {
    j.error = r.error
    return
  }
  pending.value = setPending(pending.value, j.row, j.scope, r.value)
  j.show = false
}

// ---- add variable
const add = ref({ show: false, key: '', scope: 'default', value: '', type: null as VariableType | null, error: '' })
function openAdd() {
  if (!writable.value.length) return
  add.value = { show: true, key: '', scope: writable.value[0], value: '', type: null, error: '' }
}
const addKeyRule = (v: string) => validateNewKey(v, allRows.value.map((r) => r.key))
function applyAdd() {
  const a = add.value
  const k = addKeyRule(a.key)
  if (k !== true) return void (a.error = k)
  const denied = scopeDenial(a.scope)
  if (denied) return void (a.error = denied)
  const type = a.type ?? inferType(a.value)
  const r = parseValue(type, a.value)
  if (!r.ok) return void (a.error = r.error)
  const row = newLocalRow(a.key, type)
  added.value = [...added.value, row]
  pending.value = setPending(pending.value, row, a.scope, r.value)
  a.show = false
}

// ---- save
const review = ref<{ show: boolean; busy: boolean; result: OverridesPutResult | null }>({
  show: false,
  busy: false,
  result: null,
})
const saveError = ref<ViewError | null>(null)
const saving = ref(false)

async function startSave() {
  if (!allowedPending.value.size || !app.value || !doc.value) return
  saving.value = true
  saveError.value = null
  try {
    const result = await putOverrides(app.value, { ifMatch: doc.value.etag, changes: buildChanges(allowedPending.value) }, true)
    review.value = { show: true, busy: false, result }
  } catch (e) {
    saveError.value = toError(e)
  } finally {
    saving.value = false
  }
}
async function confirmSave() {
  if (!app.value || !doc.value) return
  review.value.busy = true
  try {
    await putOverrides(app.value, { ifMatch: doc.value.etag, changes: buildChanges(allowedPending.value) }, false)
    review.value.show = false
    pending.value = emptyPending()
    added.value = []
    notify('Overrides saved')
    await load()
  } catch (e) {
    review.value.show = false
    saveError.value = toError(e)
  } finally {
    review.value.busy = false
  }
}
// Switching the context app drops the pending changes (already confirmed in the app bar).
watch(app, () => {
  ctrl?.abort()
  added.value = []
  pending.value = emptyPending()
  cancelEdit()
  doc.value = null
  error.value = null
  saveError.value = null
  loading.value = false
  void load()
})
const discard = () => confirmDiscard(() => ((pending.value = emptyPending()), (added.value = []), cancelEdit()))

// ---- cell presentation
interface CellModel {
  scope: string
  view: ReturnType<typeof cellView>
  changed: boolean
  mismatch: boolean
  /** Why the cell is read-only (null = editable). */
  denial: string | null
}
const scopeDenials = computed(() => new Map(scopes.value.map((s) => [s, scopeDenial(s)])))
function cellsOf(row: OverrideVariable): CellModel[] {
  return scopes.value.map((scope) => {
    const change = getPending(pending.value, row.key, scope)
    const view = cellView(row, scope, change)
    return { scope, view, changed: !!change, mismatch: cellMismatch(row, view), denial: scopeDenials.value.get(scope) ?? null }
  })
}
const typeOptions = VARIABLE_TYPES.map((t) => ({ title: t, value: t }))
const scopeStyle = (s: string) =>
  s === 'default' ? undefined : { '--env-color': envColorCss(s), '--env-tint': envColorCss(s, 0.12) }
</script>

<template>
  <div class="overrides-view">
    <PageHeader title="Overrides" icon="mdi-table-edit">
      <template #subtitle>
        <span v-if="app" class="font-mono">{{ app }}</span>
        <span v-if="app" aria-hidden="true">·</span>
        <span>one row per variable, one column per scope; empty cells inherit</span>
      </template>
      <template #actions>
        <PermissionTip :reason="addDenial">
          <v-btn prepend-icon="mdi-plus" :disabled="!doc || !writable.length" @click="openAdd">Add variable</v-btn>
        </PermissionTip>
        <v-btn prepend-icon="mdi-refresh" :loading="loading" :disabled="!app" @click="reload">Reload</v-btn>
      </template>
    </PageHeader>

    <NoAppState v-if="!app" message="Choose an application to edit its overrides." />

    <div v-if="app" class="toolbar mb-3">
      <v-text-field
        v-model="search"
        label="Search keys"
        prepend-inner-icon="mdi-magnify"
        clearable
        density="comfortable"
        hide-details
        autocomplete="off"
        class="toolbar__search"
      />
      <v-switch v-model="onlyOverridden" label="Only overridden" density="compact" hide-details color="primary" />
    </div>

    <v-alert v-if="error" :type="alertType(error.kind)" variant="tonal" class="my-3">
      <div class="d-flex align-center ga-2">
        <span>{{ error.message }}</span>
        <v-spacer />
        <v-btn size="small" @click="retry">Retry</v-btn>
      </div>
    </v-alert>
    <v-alert v-if="saveError" :type="alertType(saveError.kind)" variant="tonal" closable class="my-3" @click:close="saveError = null">
      <div class="d-flex align-center ga-2">
        <span>
          {{ saveError.kind === 'conflict' ? 'The overrides changed on the server since they were loaded. ' : '' }}{{ saveError.message }}
        </span>
        <v-spacer />
        <v-btn v-if="saveError.kind === 'conflict'" size="small" @click="(saveError = null), load(true)">Reload</v-btn>
      </div>
    </v-alert>

    <v-expansion-panels v-if="doc && doc.warnings.length" class="my-3">
      <v-expansion-panel>
        <v-expansion-panel-title class="text-warning">
          <v-icon start color="warning">mdi-alert-outline</v-icon>
          {{ doc.warnings.length }} warning{{ doc.warnings.length === 1 ? '' : 's' }}
        </v-expansion-panel-title>
        <v-expansion-panel-text>
          <div v-for="(w, i) in doc.warnings" :key="i" class="text-body-2 mb-1 text-break">{{ w }}</div>
        </v-expansion-panel-text>
      </v-expansion-panel>
    </v-expansion-panels>

    <v-progress-linear v-if="loading" indeterminate class="my-2" />

    <div v-if="doc && !loading && !rows.length" class="text-medium-emphasis pa-4 text-center">
      {{ allRows.length ? 'No variables match the current filters.' : 'This application has no variables yet. Use "Add variable".' }}
    </div>

    <div v-if="doc && rows.length" class="grid-scroll">
      <table class="ov-grid">
        <thead>
          <tr>
            <th class="sticky-col">Variable</th>
            <th class="lock-col" title="Secret">
              <v-icon size="small" icon="mdi-lock-outline" aria-label="Secret" />
            </th>
            <th
              v-for="s in scopes"
              :key="s"
              :class="[
                'scope-col',
                { 'scope-col--env': s !== 'default', 'scope-col--current': s === ctx.state.env, 'scope-col--readonly': !!scopeDenials.get(s) },
              ]"
              :style="scopeStyle(s)"
              :title="s === ctx.state.env ? 'Current environment' : undefined"
            >
              <span class="d-inline-flex align-center ga-2">
                <EnvDot v-if="s !== 'default'" :name="s" />
                {{ s }}
                <PermissionTip v-if="scopeDenials.get(s)" :reason="scopeDenials.get(s)">
                  <v-icon size="x-small" icon="mdi-pencil-off-outline" class="text-medium-emphasis" aria-hidden="true" />
                </PermissionTip>
              </span>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.key">
            <td class="sticky-col">
              <div class="font-mono text-break">{{ row.key }}</div>
              <div class="text-caption text-medium-emphasis">
                {{ row.type }}<span v-if="!row.declared"> - undeclared</span>
              </div>
            </td>
            <td class="lock-col">
              <v-icon
                v-if="row.secret"
                size="small"
                color="info"
                icon="mdi-lock"
                aria-label="Secret variable"
                title="Secret. Change this flag in the Variables panel of the Editor."
              />
              <v-icon v-else size="small" icon="mdi-lock-open-variant-outline" class="text-disabled" aria-label="Not secret" />
            </td>
            <td
              v-for="c in cellsOf(row)"
              :key="c.scope"
              :class="['ov-cell', { changed: c.changed, mismatch: c.mismatch, readonly: !!c.denial }]"
              :data-testid="`cell-${row.key}-${c.scope}`"
              :title="c.denial ?? undefined"
            >
              <template v-if="isEditing(row.key, c.scope)">
                <v-text-field
                  v-model="editText"
                  class="ov-input"
                  density="compact"
                  hide-details="auto"
                  :error-messages="editError"
                  :placeholder="row.secret ? 'new value' : ''"
                  :type="row.secret ? 'password' : 'text'"
                  autocomplete="off"
                  autofocus
                  @keydown.enter.prevent="commitEdit(row)"
                  @keydown.esc.prevent="cancelEdit"
                  @keydown.tab="tabNext(row, $event)"
                />
              </template>
              <div v-else class="d-flex align-center ga-1">
                <button
                  type="button"
                  class="cell-btn font-mono text-break"
                  :class="{ inherited: c.view.kind === 'inherited', empty: c.view.kind === 'empty' }"
                  :aria-label="c.denial ? `${row.key} for ${c.scope} (read-only)` : `Edit ${row.key} for ${c.scope}`"
                  :disabled="!!c.denial"
                  @click="startEdit(row, c.scope)"
                >
                  <template v-if="c.view.kind === 'empty'">-</template>
                  <template v-else>
                    <v-icon
                      v-if="c.view.kind === 'inherited'"
                      size="x-small"
                      icon="mdi-arrow-down-right"
                      :title="`from ${c.view.from}`"
                      :aria-label="`from ${c.view.from}`"
                      class="mr-1"
                    />
                    {{ c.view.text }}
                  </template>
                  <v-icon v-if="c.mismatch" size="x-small" color="error" icon="mdi-alert-circle-outline" :title="`Expected ${row.type}`" />
                </button>
                <v-btn
                  v-if="c.changed"
                  icon="mdi-undo"
                  size="x-small"
                  variant="text"
                  density="comfortable"
                  aria-label="Undo change"
                  title="Undo change"
                  @click="undoCell(row, c.scope)"
                />
                <v-btn
                  v-if="!c.denial && hasOverride(row, c.scope)"
                  icon="mdi-close"
                  size="x-small"
                  variant="text"
                  density="comfortable"
                  aria-label="Clear (inherit)"
                  title="Clear (inherit from default)"
                  @click="clearCell(row, c.scope)"
                />
              </div>
              <div v-if="c.mismatch" class="text-caption text-error">Expected {{ row.type }}</div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="doc" class="d-flex flex-wrap ga-3 mt-3 text-caption text-medium-emphasis align-center">
      <span class="d-inline-flex align-center ga-1"><span class="legend-changed" aria-hidden="true" />changed, not saved</span>
      <span class="d-inline-flex align-center ga-1"><v-icon size="x-small" icon="mdi-arrow-down-right" />inherited from default</span>
      <span class="d-inline-flex align-center ga-1"><v-icon size="x-small" icon="mdi-lock" />secret ({{ MASK }}, never shown)</span>
      <span v-if="writable.length < scopes.length" class="d-inline-flex align-center ga-1">
        <v-icon size="x-small" icon="mdi-pencil-off-outline" />read-only (no write access)
      </span>
      <v-btn size="x-small" variant="text" prepend-icon="mdi-open-in-new" @click="emit('open-editor')">
        Change secret flags in Editor &gt; Variables
      </v-btn>
    </div>

    <!-- Pending changes: sticky at the bottom of the content area -->
    <div v-if="dirty" class="pending-bar" role="region" aria-label="Pending changes">
      <v-icon icon="mdi-pencil-circle-outline" color="warning" />
      <span>
        <strong>{{ allowedPending.size }}</strong> pending change{{ allowedPending.size === 1 ? '' : 's' }}
      </span>
      <v-spacer />
      <v-btn prepend-icon="mdi-undo" @click="discard">Discard</v-btn>
      <v-btn
        color="primary"
        variant="flat"
        prepend-icon="mdi-eye-check-outline"
        :disabled="!allowedPending.size"
        :loading="saving"
        @click="startSave"
      >
        Review
      </v-btn>
    </div>

    <!-- JSON dialog -->
    <v-dialog v-model="json.show" :fullscreen="smAndDown" max-width="720">
      <v-card>
        <v-card-title class="text-subtitle-1 text-break">
          <span class="font-mono">{{ json.row?.key }}</span> ({{ json.row?.type }}) for {{ json.scope }}
        </v-card-title>
        <v-card-text>
          <div class="json-host">
            <CodeEditor v-model="json.text" language="json" :dark="dark" @save="applyJson" />
          </div>
          <div v-if="json.error" class="text-error text-caption mt-1">{{ json.error }}</div>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="json.show = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" @click="applyJson">Apply</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Add variable -->
    <v-dialog v-model="add.show" :fullscreen="smAndDown" max-width="520">
      <v-card>
        <v-card-title class="text-subtitle-1">Add variable</v-card-title>
        <v-card-text>
          <v-text-field v-model="add.key" label="Key (dotted)" :rules="[addKeyRule]" autocomplete="off" density="comfortable" />
          <v-select v-model="add.scope" :items="writable" label="Scope" density="comfortable" />
          <v-select
            v-model="add.type"
            :items="typeOptions"
            label="Type (optional, inferred from the value)"
            clearable
            density="comfortable"
          />
          <v-textarea v-model="add.value" label="Value" rows="2" auto-grow autocomplete="off" density="comfortable" class="font-mono" />
          <div v-if="add.error" class="text-error text-caption">{{ add.error }}</div>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="add.show = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" @click="applyAdd">Add</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Review -->
    <v-dialog v-model="review.show" :fullscreen="smAndDown" max-width="720" scrollable persistent>
      <v-card>
        <v-card-title class="text-subtitle-1">Review changes</v-card-title>
        <v-card-text>
          <template v-if="review.result">
            <div v-if="!review.result.files.length" class="text-medium-emphasis">The server reports no file changes.</div>
            <v-card v-for="f in review.result.files" :key="f.name" color="surface-light" class="mb-3">
              <v-card-title class="text-subtitle-2 d-flex align-center ga-2">
                <span class="font-mono text-break">{{ f.name }}</span>
                <v-chip size="x-small" label :color="f.created ? 'success' : 'warning'">{{ f.created ? 'create' : 'modify' }}</v-chip>
              </v-card-title>
              <v-card-text>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip v-for="k in f.changedKeys" :key="k" size="small" label class="font-mono">{{ k }}</v-chip>
                </div>
              </v-card-text>
            </v-card>
            <v-alert v-for="(w, i) in review.result.warnings" :key="i" type="warning" variant="tonal" density="compact" class="mb-2">
              {{ w }}
            </v-alert>
          </template>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="review.busy" @click="review.show = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" :loading="review.busy" @click="confirmSave">Confirm and save</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Unsaved changes -->
    <v-dialog v-model="guard.show" max-width="420">
      <v-card>
        <v-card-title class="text-subtitle-1">Discard unsaved overrides?</v-card-title>
        <v-card-text>{{ allowedPending.size }} pending change{{ allowedPending.size === 1 ? '' : 's' }} will be lost.</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="guard.show = false">Keep editing</v-btn>
          <v-btn color="warning" variant="flat" @click="guardConfirm">Discard</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" timeout="2000">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.overrides-view {
  min-width: 0;
}
.toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 16px;
}
.toolbar__search {
  flex: 0 1 360px;
  min-width: 200px;
}
.grid-scroll {
  overflow-x: auto;
  max-width: 100%;
  background: rgb(var(--v-theme-surface));
  border-radius: 12px;
}
.ov-grid {
  border-collapse: separate;
  border-spacing: 0;
  min-width: 100%;
}
.ov-grid th,
.ov-grid td {
  padding: 8px 12px;
  text-align: left;
  vertical-align: top;
  min-width: 140px;
  border-bottom: 1px solid rgba(var(--v-theme-on-surface), 0.06);
}
.ov-grid th {
  background: rgb(var(--v-theme-surface-light));
  font-weight: 600;
  white-space: nowrap;
}
.ov-grid .scope-col--env {
  box-shadow: inset 0 3px 0 var(--env-color);
}
.ov-grid .scope-col--current {
  background: var(--env-tint);
}
.sticky-col {
  position: sticky;
  left: 0;
  z-index: 1;
  background: rgb(var(--v-theme-surface));
  min-width: 180px;
  max-width: 260px;
  box-shadow: inset -1px 0 0 rgba(var(--v-theme-on-surface), 0.08);
}
.ov-grid th.sticky-col {
  background: rgb(var(--v-theme-surface-light));
}
.ov-grid .lock-col {
  min-width: 40px;
  width: 40px;
  text-align: center;
}
.ov-cell.changed {
  background: rgba(var(--v-theme-warning), 0.15);
}
/* Scope the caller may not write: muted fill, no edit affordance. */
.ov-cell.readonly {
  background: rgba(var(--v-theme-on-surface), 0.04);
}
.ov-cell.readonly .cell-btn {
  cursor: not-allowed;
}
.ov-cell.mismatch {
  outline: 1px solid rgb(var(--v-theme-error));
  outline-offset: -1px;
}
.cell-btn {
  background: none;
  border: 0;
  padding: 2px 4px;
  border-radius: 6px;
  color: inherit;
  text-align: left;
  cursor: pointer;
  flex: 1;
  min-width: 0;
}
.cell-btn:hover:not(:disabled) {
  background: rgba(var(--v-theme-on-surface), 0.06);
}
.cell-btn:focus-visible {
  outline: 2px solid rgb(var(--v-theme-secondary));
  outline-offset: 1px;
}
.cell-btn:disabled {
  cursor: default;
}
.cell-btn.inherited,
.cell-btn.empty {
  opacity: 0.55;
}
.legend-changed {
  display: inline-block;
  width: 12px;
  height: 12px;
  border-radius: 3px;
  background: rgba(var(--v-theme-warning), 0.35);
}
.pending-bar {
  position: sticky;
  bottom: 16px;
  z-index: 4;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
  margin-top: 16px;
  padding: 10px 12px 10px 16px;
  border-radius: 12px;
  background: rgb(var(--v-theme-surface-bright));
  box-shadow: 0 6px 24px rgba(0, 0, 0, 0.35);
}
.json-host {
  height: 320px;
}
</style>
