<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useDisplay } from 'vuetify'
import { fetchConfigText, ConfigApiError, type ConfigFormat } from '../api/config'
import { deleteFile, FilesApiError, getFile, listFiles, putFile, type ConfigFile } from '../api/files'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import AppVariablesPanel from '../components/AppVariablesPanel.vue'
import CodeEditor, { type ExternalMarker } from '../components/CodeEditor.vue'
import EnvDot from '../components/EnvDot.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import PermissionTip from '../components/PermissionTip.vue'
import { fileWriteDenial, newFileDenial } from '../utils/access'
import { overrideFiles } from '../utils/overview'
import {
  buildTemplate,
  environmentForFile,
  EXTENSIONS,
  formatForFileName,
  formatForExtension,
  formatIcon,
  isDefaultFile,
  isReservedFile,
  NAME_HINT,
  validateBaseName,
  type FileFormat,
  type TemplateKind,
} from '../utils/fileNames'

const props = defineProps<{
  dark: boolean
  /** "Open the file of this environment" request (Overview quick action); `n` makes repeats distinct. */
  openEnv?: { env: string; n: number } | null
}>()

const { mdAndUp } = useDisplay()
// The application comes from the global context (app bar).
const ctx = useContext()
// Writes follow the grants: default.* needs write on every environment of the app, {env}.* on that
// environment; "New file" needs write on at least one environment. Otherwise the editor is read-only.
const { grants } = useAuth()
const newDenial = computed(() => (ctx.state.app ? newFileDenial(grants.value, ctx.state.app) : null))
const canCreateAny = computed(() => !!ctx.state.app && newDenial.value === null)

interface ViewError {
  kind: string
  message: string
  line?: number
  column?: number
}
const toError = (e: unknown): ViewError => {
  if (e instanceof FilesApiError) return { kind: e.kind, message: e.message, line: e.line, column: e.column }
  if (e instanceof ConfigApiError) return { kind: e.kind, message: e.message }
  return { kind: 'network', message: 'Unexpected error.' }
}
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')

// ---- files of the context app
const app = computed(() => ctx.state.app)
/** Application whose file list is loaded (the "open environment" request waits for it). */
const filesFor = ref<string | null>(null)

const files = ref<ConfigFile[]>([])
const filesLoading = ref(false)
const filesError = ref<ViewError | null>(null)

let filesSeq = 0
async function loadFiles() {
  if (!app.value) return
  const n = ++filesSeq
  const a = app.value
  filesLoading.value = true
  filesError.value = null
  try {
    const list = (await listFiles(a)).filter((f) => !isReservedFile(f.name))
    if (n === filesSeq) files.value = list
  } catch (e) {
    if (n !== filesSeq) return
    filesError.value = toError(e)
    files.value = []
  } finally {
    if (n === filesSeq) {
      filesLoading.value = false
      filesFor.value = a
      tryOpenEnv()
    }
  }
}

// ---- current document
const selected = ref<string | null>(null)
const isNew = ref(false)
const original = ref('')
const content = ref('')
const docLoading = ref(false)
const docError = ref<ViewError | null>(null)
const saving = ref(false)
const saveError = ref<ViewError | null>(null)
const deleting = ref(false)
// Bumped after config files change so the variables status panel refreshes.
const statusRefresh = ref(0)

const dirty = computed(() => selected.value !== null && (isNew.value || content.value !== original.value))
/** Why the selected file cannot be saved or deleted (null = allowed). */
const fileDenial = computed(() =>
  app.value && selected.value ? fileWriteDenial(grants.value, app.value, selected.value) : null,
)
const canWrite = computed(() => !!selected.value && fileDenial.value === null)
const language = computed<FileFormat>(() => (selected.value ? formatForFileName(selected.value) : null) ?? 'json')
const markers = computed<ExternalMarker[]>(() =>
  saveError.value?.kind === 'bad-request' && saveError.value.line
    ? [{ line: saveError.value.line, column: saveError.value.column, message: saveError.value.message }]
    : [],
)
watch(content, () => {
  if (saveError.value?.kind === 'bad-request') saveError.value = null
})

const fileItems = computed(() => {
  const list = [...files.value]
  if (isNew.value && selected.value && !list.some((f) => f.name === selected.value)) {
    list.push({ name: selected.value, format: language.value, size: 0 })
  }
  return list.sort((a, b) => a.name.localeCompare(b.name))
})

let docCtrl: AbortController | undefined
async function openFile(name: string) {
  if (!app.value) return
  docCtrl?.abort()
  const ctrl = (docCtrl = new AbortController())
  selected.value = name
  isNew.value = false
  docLoading.value = true
  docError.value = null
  saveError.value = null
  try {
    const text = await getFile(app.value, name, { signal: ctrl.signal })
    if (ctrl.signal.aborted) return
    original.value = text
    content.value = text
    syncPreviewEnv(name)
    void loadPreview()
  } catch (e) {
    if (ctrl.signal.aborted) return
    docError.value = toError(e)
    original.value = ''
    content.value = ''
  } finally {
    if (!ctrl.signal.aborted) docLoading.value = false
  }
}

// ---- unsaved changes guard
const discardDialog = ref(false)
let pendingAction: (() => void) | null = null
function guarded(action: () => void) {
  if (!dirty.value) return action()
  pendingAction = action
  discardDialog.value = true
}
function confirmDiscard() {
  discardDialog.value = false
  const a = pendingAction
  pendingAction = null
  a?.()
}
function onBeforeUnload(e: BeforeUnloadEvent) {
  if (dirty.value) {
    e.preventDefault()
    e.returnValue = ''
  }
}
onMounted(() => {
  window.addEventListener('beforeunload', onBeforeUnload)
  void loadFiles()
})
// The app bar asks before switching the application while the file has unsaved changes.
const unregister = ctx.registerUnsaved('Editor', () => dirty.value)
onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', onBeforeUnload)
  unregister()
})

function clearSelection() {
  selected.value = null
  isNew.value = false
  original.value = ''
  content.value = ''
  docError.value = null
  saveError.value = null
}

// Switching the context app (already confirmed in the app bar) resets the editor.
watch(app, () => {
  docCtrl?.abort()
  clearSelection()
  docLoading.value = false
  files.value = []
  filesFor.value = null
  filesError.value = null
  preview.value = { state: 'idle' }
  void loadFiles()
})

function selectFile(name: string | null) {
  if (!name || name === selected.value) return
  guarded(() => void openFile(name))
}
const fileModel = computed({
  get: () => selected.value,
  set: (v: string | null) => selectFile(v),
})

// ---- save / revert / delete
const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

async function save() {
  if (!canWrite.value || !app.value || !selected.value || saving.value || docLoading.value) return
  saving.value = true
  saveError.value = null
  try {
    const result = await putFile(app.value, selected.value, content.value)
    original.value = content.value
    isNew.value = false
    notify(result === 'created' ? 'File created' : 'File saved')
    void loadFiles()
    void loadPreview()
    statusRefresh.value++
  } catch (e) {
    saveError.value = toError(e)
  } finally {
    saving.value = false
  }
}

function revert() {
  content.value = original.value
  saveError.value = null
}

const deleteDialog = ref(false)
async function confirmDelete() {
  if (!app.value || !selected.value) return
  if (isNew.value) {
    deleteDialog.value = false
    clearSelection()
    return
  }
  deleting.value = true
  try {
    await deleteFile(app.value, selected.value)
    deleteDialog.value = false
    notify(`Deleted ${selected.value}`)
    clearSelection()
    void loadFiles()
    statusRefresh.value++
  } catch (e) {
    deleteDialog.value = false
    saveError.value = toError(e)
  } finally {
    deleting.value = false
  }
}

// ---- new file dialog
const newDialog = ref(false)
const newBase = ref('')
const newExt = ref<string>('.json')
const newTemplate = ref<TemplateKind>('empty')
const newBusy = ref(false)
const newError = ref<string | null>(null)

const defaultFile = computed(() => files.value.find((f) => isDefaultFile(f.name)))
const newFormat = computed(() => formatForExtension(newExt.value) ?? 'json')
const copyAvailable = computed(() => !!defaultFile.value)
const newName = computed(() => `${newBase.value}${newExt.value}`)
const newNameError = computed(() => {
  if (!newBase.value) return null
  const e = validateBaseName(newBase.value)
  if (e) return e
  const clash = files.value.some((f) => f.name.toLowerCase() === newName.value.toLowerCase())
  if (clash) return 'A file with this name already exists'
  return app.value ? fileWriteDenial(grants.value, app.value, newName.value) : null
})
const canCreate = computed(() => !!newBase.value && !newNameError.value && !newBusy.value)

function openNewDialog(base = '') {
  guarded(() => {
    newBase.value = base
    newExt.value = '.json'
    newTemplate.value = 'empty'
    newError.value = null
    newDialog.value = true
  })
}

async function createFile() {
  if (!canCreate.value || !app.value) return
  newBusy.value = true
  newError.value = null
  try {
    let defaultText: string | undefined
    if (newTemplate.value === 'copy-default' && defaultFile.value) {
      defaultText = await getFile(app.value, defaultFile.value.name)
    }
    docCtrl?.abort()
    selected.value = newName.value
    isNew.value = true
    original.value = ''
    content.value = buildTemplate(newTemplate.value, newFormat.value, defaultText)
    docError.value = null
    docLoading.value = false
    saveError.value = null
    syncPreviewEnv(newName.value)
    newDialog.value = false
  } catch (e) {
    newError.value = toError(e).message
  } finally {
    newBusy.value = false
  }
}

// ---- preview (environment defaults to the context environment)
const showPreview = ref(true)
const previewEnv = ref(ctx.state.env ?? '')
const previewFormat = ref<ConfigFormat>('json')
const preview = ref<{ state: 'idle' | 'loading' | 'ok' | 'error'; text?: string; error?: ViewError }>({ state: 'idle' })
const envItems = computed(() =>
  files.value.map((f) => environmentForFile(f.name)).filter((e): e is string => !!e),
)
function syncPreviewEnv(name: string) {
  const env = environmentForFile(name)
  if (env) previewEnv.value = env
}
let previewCtrl: AbortController | undefined
async function loadPreview() {
  const env = previewEnv.value.trim()
  if (!app.value || !env || !showPreview.value) {
    preview.value = { state: 'idle' }
    return
  }
  previewCtrl?.abort()
  const ctrl = (previewCtrl = new AbortController())
  preview.value = { state: 'loading' }
  try {
    const text = await fetchConfigText(app.value, env, previewFormat.value, { signal: ctrl.signal })
    if (!ctrl.signal.aborted) preview.value = { state: 'ok', text }
  } catch (e) {
    if (!ctrl.signal.aborted) preview.value = { state: 'error', error: toError(e) }
  }
}
watch([previewFormat, showPreview], () => void loadPreview())
watch(
  () => ctx.state.env,
  (env) => {
    if (!env || env === previewEnv.value) return
    previewEnv.value = env
    void loadPreview()
  },
)

// ---- "open the file of environment X" (Overview quick action)
const openEnvRequest = ref<string | null>(null)
watch(
  () => props.openEnv,
  (r) => {
    if (!r) return
    openEnvRequest.value = r.env
    tryOpenEnv()
  },
  { immediate: true },
)
function tryOpenEnv() {
  const env = openEnvRequest.value
  if (!env || !app.value || filesLoading.value || filesFor.value !== app.value) return
  openEnvRequest.value = null
  previewEnv.value = env
  const file = overrideFiles(files.value.map((f) => f.name)).get(env)
  if (file) selectFile(file)
  else if (fileWriteDenial(grants.value, app.value, env) === null) openNewDialog(env)
}

const saveShortcut = computed(() => (/Mac|iPhone|iPad/.test(navigator.platform) ? 'Cmd+S' : 'Ctrl+S'))
const extItems: string[] = [...EXTENSIONS]
</script>

<template>
  <div class="config-editor">
    <PageHeader title="Editor" icon="mdi-file-code-outline">
      <template #subtitle>
        <span v-if="app" class="font-mono">{{ app }}</span>
        <span v-if="app" aria-hidden="true">·</span>
        <span>raw config files (advanced) and declared variables</span>
      </template>
      <template v-if="app" #actions>
        <v-btn prepend-icon="mdi-refresh" :loading="filesLoading" @click="loadFiles">Reload files</v-btn>
        <PermissionTip :reason="newDenial">
          <v-btn prepend-icon="mdi-plus" :disabled="!canCreateAny" @click="openNewDialog()">New file</v-btn>
        </PermissionTip>
      </template>
    </PageHeader>

    <NoAppState v-if="!app" message="Choose an application to edit its files." />

    <template v-else>
      <AppVariablesPanel :app="app" :refresh-token="statusRefresh" />

      <v-row dense>
        <!-- Files -->
        <v-col cols="12" md="3">
          <v-alert v-if="filesError" :type="alertType(filesError.kind)" variant="tonal" density="compact" class="mb-2">
            {{ filesError.message }}
            <template #append><v-btn size="x-small" @click="loadFiles">Retry</v-btn></template>
          </v-alert>
          <v-progress-linear v-if="filesLoading" indeterminate class="mb-2" />
          <v-select
            v-if="!mdAndUp"
            v-model="fileModel"
            :items="fileItems"
            item-title="name"
            item-value="name"
            label="File"
            density="comfortable"
            no-data-text="No files"
          >
            <template #item="{ props: itemProps, item }">
              <v-list-item v-bind="itemProps" :prepend-icon="formatIcon(item.raw.format)" />
            </template>
          </v-select>
          <v-card v-else>
            <v-list density="compact" nav color="primary" aria-label="Files">
              <v-list-subheader>Files</v-list-subheader>
              <v-list-item v-if="!fileItems.length && !filesLoading" disabled title="No files" />
              <v-list-item
                v-for="f in fileItems"
                :key="f.name"
                :value="f.name"
                :active="f.name === selected"
                :prepend-icon="formatIcon(f.format)"
                :title="f.name"
                :subtitle="f.size ? `${f.size} B` : 'unsaved'"
                rounded="lg"
                @click="selectFile(f.name)"
              >
                <template v-if="environmentForFile(f.name)" #append>
                  <EnvDot :name="environmentForFile(f.name)!" />
                </template>
              </v-list-item>
            </v-list>
          </v-card>
        </v-col>

        <!-- Editor -->
        <v-col cols="12" :md="showPreview ? 5 : 9" class="editor-col">
          <v-card class="editor-card">
            <v-card-title class="d-flex align-center flex-wrap ga-2 text-subtitle-1">
              <span class="text-truncate font-mono">{{ selected ?? 'No file selected' }}</span>
              <v-chip v-if="dirty" size="x-small" color="warning" label>unsaved</v-chip>
              <v-spacer />
              <v-btn
                size="small"
                variant="text"
                :icon="showPreview ? 'mdi-eye-off-outline' : 'mdi-eye-outline'"
                :aria-label="showPreview ? 'Hide preview' : 'Show preview'"
                @click="showPreview = !showPreview"
              />
            </v-card-title>
            <v-card-text>
              <v-progress-linear v-if="docLoading" indeterminate />
              <v-alert v-else-if="docError" :type="alertType(docError.kind)" variant="tonal">
                {{ docError.message }}
              </v-alert>
              <div v-else-if="!selected" class="text-medium-emphasis">Select a file, or create a new one.</div>
              <template v-else>
                <v-alert
                  v-if="saveError"
                  :type="alertType(saveError.kind)"
                  :title="saveError.kind === 'bad-request' ? 'Validation error' : undefined"
                  variant="tonal"
                  class="mb-3"
                  closable
                  @click:close="saveError = null"
                >
                  {{ saveError.message }}
                  <span v-if="saveError.line"> (line {{ saveError.line }}<template v-if="saveError.column">, column {{ saveError.column }}</template>)</span>
                </v-alert>
                <CodeEditor
                  v-model="content"
                  :language="language"
                  :dark="dark"
                  :markers="markers"
                  :read-only="!canWrite"
                  class="main-editor"
                  @save="save"
                />
                <div class="d-flex flex-wrap ga-2 mt-3">
                  <PermissionTip :reason="fileDenial">
                    <v-btn
                      color="primary"
                      variant="flat"
                      prepend-icon="mdi-content-save-outline"
                      :loading="saving"
                      :disabled="!canWrite || !dirty || saving"
                      @click="save"
                    >
                      Save
                      <v-tooltip activator="parent" location="top">{{ saveShortcut }}</v-tooltip>
                    </v-btn>
                  </PermissionTip>
                  <v-btn prepend-icon="mdi-undo" :disabled="!dirty || saving" @click="revert">Revert</v-btn>
                  <v-spacer />
                  <PermissionTip :reason="fileDenial">
                    <v-btn color="error" prepend-icon="mdi-delete-outline" :disabled="!canWrite || saving" @click="deleteDialog = true">
                      Delete
                    </v-btn>
                  </PermissionTip>
                </div>
              </template>
            </v-card-text>
          </v-card>
        </v-col>

        <!-- Preview -->
        <v-col v-if="showPreview" cols="12" md="4" class="editor-col">
          <v-card class="editor-card">
            <v-card-title class="text-subtitle-1">Preview</v-card-title>
            <v-card-text>
              <v-row dense>
                <v-col cols="12" sm="7">
                  <v-combobox
                    v-model="previewEnv"
                    :items="envItems"
                    label="Environment"
                    density="comfortable"
                    hide-details
                    @update:model-value="loadPreview"
                  />
                </v-col>
                <v-col cols="12" sm="5">
                  <v-select
                    v-model="previewFormat"
                    :items="['json', 'yaml', 'env']"
                    label="Format"
                    density="comfortable"
                    hide-details
                  />
                </v-col>
              </v-row>
              <div class="mt-3">
                <v-progress-linear v-if="preview.state === 'loading'" indeterminate />
                <v-alert v-else-if="preview.state === 'error'" :type="alertType(preview.error!.kind)" variant="tonal">
                  {{ preview.error!.message }}
                </v-alert>
                <CodeEditor
                  v-else-if="preview.state === 'ok'"
                  :model-value="preview.text ?? ''"
                  :language="previewFormat"
                  :dark="dark"
                  read-only
                  :lint="false"
                  class="preview-editor"
                />
                <div v-else class="text-medium-emphasis">Pick an environment to preview the merged result.</div>
              </div>
            </v-card-text>
          </v-card>
        </v-col>
      </v-row>
    </template>

    <!-- New file -->
    <v-dialog v-model="newDialog" max-width="480">
      <v-card title="New file">
        <v-card-text>
          <v-row dense>
            <v-col cols="7">
              <v-text-field
                v-model="newBase"
                label="Name"
                autocomplete="off"
                :error-messages="newNameError ?? undefined"
                autofocus
                @keyup.enter="createFile"
              />
            </v-col>
            <v-col cols="5">
              <v-select v-model="newExt" :items="extItems" label="Extension" />
            </v-col>
          </v-row>
          <div class="text-caption text-medium-emphasis mb-2">{{ NAME_HINT }}</div>
          <v-radio-group v-model="newTemplate" label="Template" density="compact" hide-details>
            <v-radio value="empty" label="Empty" />
            <v-radio value="copy-default" :disabled="!copyAvailable" label="Copy of the default file" />
          </v-radio-group>
          <v-alert v-if="newError" type="error" variant="tonal" class="mt-3">{{ newError }}</v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="newDialog = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" :disabled="!canCreate" :loading="newBusy" @click="createFile">Create</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Delete confirm -->
    <v-dialog v-model="deleteDialog" max-width="420">
      <v-card title="Delete file">
        <v-card-text>Delete <strong>{{ selected }}</strong>? This cannot be undone.</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="deleteDialog = false">Cancel</v-btn>
          <v-btn color="error" variant="flat" :loading="deleting" @click="confirmDelete">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Unsaved changes -->
    <v-dialog v-model="discardDialog" max-width="420">
      <v-card title="Unsaved changes">
        <v-card-text>You have unsaved changes. Discard them?</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="discardDialog = false">Keep editing</v-btn>
          <v-btn color="error" variant="flat" @click="confirmDiscard">Discard</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" timeout="2500">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.config-editor {
  min-width: 0;
}
.editor-col {
  min-width: 0;
}
.editor-card {
  min-width: 0;
}
.main-editor {
  height: 60vh;
}
.preview-editor {
  height: 50vh;
}
</style>
