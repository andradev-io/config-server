<script setup lang="ts">
import { computed, onMounted, ref, shallowRef, watch } from 'vue'
import { getDisplayApiBaseUrl } from '../api/config'
import { listEnvironments } from '../api/environments'
import { FilesApiError, listApps } from '../api/files'
import { ALL } from '../api/keys'
import {
  createOidcKey,
  deleteOidcKey,
  listOidcKeys,
  rotateOidcKey,
  type IssuedOidcKey,
  type OidcKey,
} from '../api/oidcKeys'
import { formatInstant, normalizeAllSelection } from '../utils/keys'
import {
  buildOidcRequest,
  emptyOidcKeyForm,
  filterOidcKeys,
  grantLabel,
  grantLimitError,
  OIDC_STATUS_COLOR,
  oidcStatus,
  oidcUsageSnippet,
  summarizeForm,
  validateOidcName,
  type OidcKeyForm,
} from '../utils/oidcKeys'

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')

const keys = ref<OidcKey[]>([])
const loading = ref(false)
const error = ref<ViewError | null>(null)
const search = ref('')
const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

/** 501: the server has no identity management configured, so there is nothing to list or create. */
const available = computed(() => error.value?.kind !== 'read-only')
const rows = computed(() => filterOidcKeys(keys.value, search.value))

async function load() {
  loading.value = true
  error.value = null
  try {
    keys.value = await listOidcKeys()
  } catch (e) {
    error.value = toError(e)
    keys.value = []
  } finally {
    loading.value = false
  }
}

// Apps / environments for the create dialog (best effort: "All" is always available).
const apps = ref<string[]>([])
const envs = ref<string[]>([])
async function loadScopes() {
  const [a, e] = await Promise.allSettled([listApps(), listEnvironments()])
  apps.value = a.status === 'fulfilled' ? a.value : []
  envs.value =
    e.status === 'fulfilled'
      ? [...e.value].sort((x, y) => x.order - y.order || x.name.localeCompare(y.name)).map((x) => x.name)
      : []
}
onMounted(() => {
  void load()
  void loadScopes()
})

const withAll = (list: string[]) => [{ title: 'All', value: ALL }, ...list.map((x) => ({ title: x, value: x }))]
const appItems = computed(() => withAll(apps.value))
const envItems = computed(() => withAll(envs.value))

// ---- create
const createOpen = ref(false)
const form = ref<OidcKeyForm>(emptyOidcKeyForm())
const createBusy = ref(false)
const createError = ref<ViewError | null>(null)
const nameTouched = ref(false)

const nameError = computed(() => (nameTouched.value ? validateOidcName(form.value.name) : null))
const limitError = computed(() => grantLimitError(form.value))
const summary = computed(() => summarizeForm(form.value))
const canCreate = computed(() => !validateOidcName(form.value.name) && !limitError.value && !createBusy.value)
const appsModel = computed({
  get: () => form.value.apps,
  set: (v: string[]) => (form.value.apps = normalizeAllSelection(form.value.apps, v)),
})
const envsModel = computed({
  get: () => form.value.environments,
  set: (v: string[]) => (form.value.environments = normalizeAllSelection(form.value.environments, v)),
})

function openCreate() {
  form.value = emptyOidcKeyForm()
  nameTouched.value = false
  createError.value = null
  createOpen.value = true
  void loadScopes()
}
async function create() {
  nameTouched.value = true
  if (!canCreate.value) return
  createBusy.value = true
  createError.value = null
  try {
    const result = await createOidcKey(buildOidcRequest(form.value))
    createOpen.value = false
    showSecret('created', result)
    void load()
  } catch (e) {
    createError.value = toError(e)
  } finally {
    createBusy.value = false
  }
}

// ---- rotate / delete (confirm first)
const confirm = ref<{ show: boolean; action: 'delete' | 'rotate'; key: OidcKey | null; busy: boolean; error: ViewError | null }>({
  show: false,
  action: 'delete',
  key: null,
  busy: false,
  error: null,
})
function askConfirm(action: 'delete' | 'rotate', key: OidcKey) {
  confirm.value = { show: true, action, key, busy: false, error: null }
}
async function runConfirm() {
  const c = confirm.value
  if (!c.key || c.busy) return
  c.busy = true
  c.error = null
  try {
    if (c.action === 'delete') {
      await deleteOidcKey(c.key.id)
      notify(`OIDC API key ${c.key.name} deleted`)
    } else {
      showSecret('rotated', await rotateOidcKey(c.key.id))
    }
    c.show = false
    void load()
  } catch (e) {
    c.error = toError(e)
  } finally {
    c.busy = false
  }
}

// ---- one-time secret (kept only in memory, cleared when the dialog closes)
const secretOpen = ref(false)
const issued = shallowRef<IssuedOidcKey | null>(null)
const issuedAction = ref<'created' | 'rotated'>('created')
const apiBase = getDisplayApiBaseUrl()
const snippet = computed(() =>
  issued.value
    ? oidcUsageSnippet(issued.value.secret, issued.value.clientId, issued.value.tokenEndpoint, apiBase, issued.value.client.grants)
    : '',
)

function showSecret(action: 'created' | 'rotated', value: IssuedOidcKey) {
  issuedAction.value = action
  issued.value = value
  secretOpen.value = true
}
watch(secretOpen, (open) => {
  if (!open) issued.value = null
})

async function copy(text: string) {
  try {
    await navigator.clipboard.writeText(text)
    notify('Copied to clipboard')
  } catch {
    notify('Copy failed (clipboard unavailable)')
  }
}

defineExpose({ load, openCreate, loading, available })
</script>

<template>
  <div class="oidc-keys">
    <v-row dense class="mb-1">
      <v-col cols="12" sm="6" md="4">
        <v-text-field
          v-model="search"
          label="Filter by name or client id"
          prepend-inner-icon="mdi-magnify"
          clearable
          density="comfortable"
          hide-details
          autocomplete="off"
        />
      </v-col>
    </v-row>

    <v-alert v-if="error" :type="alertType(error.kind)" variant="tonal" class="mb-3">
      <div class="d-flex align-center ga-2">
        <span>{{ error.message }}</span>
        <v-spacer />
        <v-btn v-if="available" size="small" @click="load">Retry</v-btn>
      </div>
      <div v-if="!available" class="text-caption mt-1">
        OIDC API keys are created in your identity provider. Configure
        <span class="font-mono">IdentityAdmin</span> on the server to manage them here.
      </div>
    </v-alert>

    <v-progress-linear v-if="loading" indeterminate class="mb-2" />

    <div v-if="!loading && !error && !rows.length" class="text-medium-emphasis pa-4 text-center">
      {{ keys.length ? 'No OIDC API keys match the filter.' : 'No OIDC API keys yet. Create one with "New API key".' }}
    </div>

    <div v-if="rows.length">
      <v-card class="table-scroll">
        <v-table density="comfortable">
          <thead>
            <tr>
              <th>Name</th>
              <th>Client id</th>
              <th>Access</th>
              <th>Status</th>
              <th>Created</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr v-for="k in rows" :key="k.id">
              <td class="text-break col-name">{{ k.name }}</td>
              <td class="font-mono text-no-wrap">{{ k.clientId }}</td>
              <td>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip v-for="g in k.grants" :key="g.role" size="x-small" label class="font-mono">{{ grantLabel(g) }}</v-chip>
                  <span v-if="!k.grants.length" class="text-caption text-medium-emphasis">no access</span>
                </div>
              </td>
              <td>
                <v-chip size="small" label :color="OIDC_STATUS_COLOR[oidcStatus(k)]">{{ oidcStatus(k) }}</v-chip>
              </td>
              <td class="text-no-wrap">{{ formatInstant(k.createdAt) }}</td>
              <td class="text-no-wrap text-right">
                <v-btn
                  icon="mdi-autorenew"
                  size="small"
                  variant="text"
                  :aria-label="`Rotate ${k.name}`"
                  title="Rotate secret"
                  @click="askConfirm('rotate', k)"
                />
                <v-btn
                  icon="mdi-delete-outline"
                  size="small"
                  variant="text"
                  color="error"
                  :aria-label="`Delete ${k.name}`"
                  title="Delete"
                  @click="askConfirm('delete', k)"
                />
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-card>
      <div class="text-caption text-medium-emphasis mt-2">
        A client requests a token with its client id and secret (client credentials) and sends it as
        <span class="font-mono">Authorization: Bearer</span>. Changes to a key's access are managed in the identity
        provider. Secrets are shown only once, when a key is created or rotated.
      </div>
    </div>

    <!-- New -->
    <v-dialog v-model="createOpen" max-width="560" scrollable>
      <v-card title="New OIDC API key">
        <v-card-text>
          <v-text-field
            v-model="form.name"
            label="Name"
            autocomplete="off"
            autofocus
            :error-messages="nameError ?? undefined"
            hint="What uses this key, e.g. ci-pipeline or billing-service."
            persistent-hint
            class="mb-3"
            @blur="nameTouched = true"
            @keyup.enter="create"
          />
          <v-radio-group v-model="form.permission" label="Permission" inline density="compact" hide-details class="mb-3">
            <v-radio value="read" label="read" />
            <v-radio value="write" label="write" />
          </v-radio-group>
          <v-select
            v-model="appsModel"
            :items="appItems"
            label="Applications"
            multiple
            chips
            closable-chips
            hint="All, or only the selected applications."
            persistent-hint
            class="mb-3"
          />
          <v-select
            v-model="envsModel"
            :items="envItems"
            label="Environments"
            multiple
            chips
            closable-chips
            hint="All, or only the selected environments."
            persistent-hint
            class="mb-3"
          />
          <div class="text-body-2 text-medium-emphasis">Grants: {{ summary }}.</div>
          <v-alert v-if="limitError" type="warning" variant="tonal" density="compact" class="mt-3">{{ limitError }}</v-alert>
          <v-alert v-if="createError" :type="alertType(createError.kind)" variant="tonal" class="mt-3">
            {{ createError.message }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="createOpen = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" :disabled="!canCreate" :loading="createBusy" @click="create">Create</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Rotate / delete confirm -->
    <v-dialog v-model="confirm.show" max-width="460">
      <v-card :title="confirm.action === 'delete' ? 'Delete OIDC API key' : 'Rotate OIDC API key'">
        <v-card-text>
          <p v-if="confirm.action === 'delete'">
            Delete <strong>{{ confirm.key?.name }}</strong>? Clients using it stop getting tokens, and tokens already
            issued expire on their own. This cannot be undone.
          </p>
          <p v-else>
            Rotate <strong>{{ confirm.key?.name }}</strong
            >? A new secret is issued and the current one stops working.
          </p>
          <v-alert v-if="confirm.error" :type="alertType(confirm.error.kind)" variant="tonal" class="mt-3">
            {{ confirm.error.message }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="confirm.busy" @click="confirm.show = false">Cancel</v-btn>
          <v-btn
            :color="confirm.action === 'delete' ? 'error' : 'warning'"
            variant="flat"
            :loading="confirm.busy"
            @click="runConfirm"
          >
            {{ confirm.action === 'delete' ? 'Delete' : 'Rotate' }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- One-time secret -->
    <v-dialog v-model="secretOpen" max-width="680" persistent scrollable>
      <v-card :title="issuedAction === 'created' ? 'OIDC API key created' : 'OIDC API key rotated'">
        <v-card-text v-if="issued">
          <v-alert type="warning" variant="tonal" class="mb-3" title="Shown only once">
            Copy the secret now and store it securely. It cannot be retrieved again; if it is lost, rotate the key.
          </v-alert>
          <div class="text-caption text-medium-emphasis mb-1">Client id</div>
          <v-sheet color="surface-light" rounded="lg" class="d-flex align-center ga-2 pa-2 mb-3">
            <code class="font-mono text-break flex-grow-1 secret">{{ issued.clientId }}</code>
            <v-btn size="small" prepend-icon="mdi-content-copy" @click="copy(issued.clientId)">Copy</v-btn>
          </v-sheet>
          <div class="text-caption text-medium-emphasis mb-1">Client secret</div>
          <v-sheet color="surface-light" rounded="lg" class="d-flex align-center ga-2 pa-2 mb-3">
            <code class="font-mono text-break flex-grow-1 secret">{{ issued.secret }}</code>
            <v-btn size="small" prepend-icon="mdi-content-copy" @click="copy(issued.secret)">Copy</v-btn>
          </v-sheet>
          <div v-if="issued.tokenEndpoint" class="text-caption text-medium-emphasis mb-1">Token endpoint</div>
          <v-sheet v-if="issued.tokenEndpoint" color="surface-light" rounded="lg" class="d-flex align-center ga-2 pa-2 mb-3">
            <code class="font-mono text-break flex-grow-1 secret">{{ issued.tokenEndpoint }}</code>
            <v-btn size="small" prepend-icon="mdi-content-copy" @click="copy(issued.tokenEndpoint)">Copy</v-btn>
          </v-sheet>
          <div class="text-caption text-medium-emphasis mb-1">Usage</div>
          <v-sheet color="surface-light" rounded="lg" class="d-flex align-center ga-2 pa-2">
            <code class="font-mono text-break flex-grow-1 secret">{{ snippet }}</code>
            <v-btn size="small" prepend-icon="mdi-content-copy" @click="copy(snippet)">Copy</v-btn>
          </v-sheet>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn color="primary" variant="flat" @click="secretOpen = false">Done</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" timeout="2500">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.oidc-keys {
  min-width: 0;
}
.table-scroll {
  overflow-x: auto;
}
.col-name {
  min-width: 140px;
}
.secret {
  white-space: pre-wrap;
  word-break: break-all;
  user-select: all;
}
</style>
