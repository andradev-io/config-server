<script setup lang="ts">
import { computed, onMounted, ref, shallowRef, watch } from 'vue'
import { getDisplayApiBaseUrl } from '../api/config'
import { listEnvironments } from '../api/environments'
import { FilesApiError, listApps } from '../api/files'
import EnvDot from '../components/EnvDot.vue'
import OidcKeysPanel from '../components/OidcKeysPanel.vue'
import PageHeader from '../components/PageHeader.vue'
import { ALL, createKey, listKeys, revokeKey, rotateKey, type ApiKey, type IssuedKey } from '../api/keys'
import {
  buildCreateRequest,
  canChangeKey,
  dateInputValue,
  emptyKeyForm,
  expiryError,
  filterKeys,
  formatInstant,
  keyDisplay,
  normalizeAllSelection,
  PERMISSION_COLOR,
  scopeChips,
  STATUS_COLOR,
  STATUS_ICON,
  usageSnippet,
  validateKeyName,
  type KeyForm,
} from '../utils/keys'

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')

const origin = ref<'local' | 'oidc'>('local')
const oidc = ref<InstanceType<typeof OidcKeysPanel> | null>(null)

const keys = ref<ApiKey[]>([])
const loading = ref(false)
const error = ref<ViewError | null>(null)
const search = ref('')
const hideRevoked = ref(false)
const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

const rows = computed(() => filterKeys(keys.value, { search: search.value, hideRevoked: hideRevoked.value }))

async function load() {
  loading.value = true
  error.value = null
  try {
    keys.value = await listKeys()
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
const form = ref<KeyForm>(emptyKeyForm())
const createBusy = ref(false)
const createError = ref<ViewError | null>(null)
const nameTouched = ref(false)
const today = ref(dateInputValue(new Date()))

const nameError = computed(() => (nameTouched.value ? validateKeyName(form.value.name) : null))
const expiresError = computed(() => expiryError(form.value.expires))
const canCreate = computed(
  () => !validateKeyName(form.value.name) && !expiresError.value && !createBusy.value,
)
const appsModel = computed({
  get: () => form.value.apps,
  set: (v: string[]) => (form.value.apps = normalizeAllSelection(form.value.apps, v)),
})
const envsModel = computed({
  get: () => form.value.environments,
  set: (v: string[]) => (form.value.environments = normalizeAllSelection(form.value.environments, v)),
})
// The clear icon sets null.
const expiresModel = computed({
  get: () => form.value.expires,
  set: (v: string | null) => (form.value.expires = v ?? ''),
})

function openCreate() {
  form.value = emptyKeyForm()
  nameTouched.value = false
  createError.value = null
  today.value = dateInputValue(new Date())
  createOpen.value = true
  void loadScopes()
}
async function create() {
  nameTouched.value = true
  if (!canCreate.value) return
  createBusy.value = true
  createError.value = null
  try {
    const issued = await createKey(buildCreateRequest(form.value))
    createOpen.value = false
    showSecret('created', issued)
    void load()
  } catch (e) {
    createError.value = toError(e)
  } finally {
    createBusy.value = false
  }
}

// ---- revoke / rotate (confirm first)
const confirm = ref<{ show: boolean; action: 'revoke' | 'rotate'; key: ApiKey | null; busy: boolean; error: ViewError | null }>({
  show: false,
  action: 'revoke',
  key: null,
  busy: false,
  error: null,
})
function askConfirm(action: 'revoke' | 'rotate', key: ApiKey) {
  confirm.value = { show: true, action, key, busy: false, error: null }
}
async function runConfirm() {
  const c = confirm.value
  if (!c.key || c.busy) return
  c.busy = true
  c.error = null
  try {
    if (c.action === 'revoke') {
      await revokeKey(c.key.id)
      notify(`API key ${c.key.name} revoked`)
    } else {
      showSecret('rotated', await rotateKey(c.key.id))
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
const issued = shallowRef<IssuedKey | null>(null)
const issuedAction = ref<'created' | 'rotated'>('created')
const apiBase = getDisplayApiBaseUrl()
const snippet = computed(() => (issued.value ? usageSnippet(issued.value.secret, apiBase, issued.value.key) : ''))

function showSecret(action: 'created' | 'rotated', value: IssuedKey) {
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
</script>

<template>
  <div class="keys-view">
    <PageHeader
      title="API Keys"
      icon="mdi-key-chain-variant"
      subtitle="Keys for clients and pipelines. Local keys use the X-Api-Key header; OIDC keys are credentials in the identity provider. Secrets are shown only once."
    >
      <template #actions>
        <template v-if="origin === 'local'">
          <v-btn prepend-icon="mdi-refresh" :loading="loading" @click="load">Refresh</v-btn>
          <v-btn color="primary" variant="flat" prepend-icon="mdi-plus" @click="openCreate">New API key</v-btn>
        </template>
        <template v-else>
          <v-btn prepend-icon="mdi-refresh" :loading="oidc?.loading" @click="oidc?.load()">Refresh</v-btn>
          <v-btn
            color="primary"
            variant="flat"
            prepend-icon="mdi-plus"
            :disabled="oidc ? !oidc.available : false"
            @click="oidc?.openCreate()"
          >
            New API key
          </v-btn>
        </template>
      </template>
    </PageHeader>

    <v-btn-toggle v-model="origin" mandatory density="comfortable" divided class="mb-3" aria-label="Key origin">
      <v-btn value="local" prepend-icon="mdi-database-lock-outline">Local</v-btn>
      <v-btn value="oidc" prepend-icon="mdi-shield-key-outline">OIDC</v-btn>
    </v-btn-toggle>

    <OidcKeysPanel v-if="origin === 'oidc'" ref="oidc" />

    <template v-if="origin === 'local'">
    <v-row dense class="mb-1">
      <v-col cols="12" sm="6" md="4">
        <v-text-field
          v-model="search"
          label="Filter by name or prefix"
          prepend-inner-icon="mdi-magnify"
          clearable
          density="comfortable"
          hide-details
          autocomplete="off"
        />
      </v-col>
      <v-col cols="12" sm="6" md="4" class="d-flex align-center">
        <v-switch v-model="hideRevoked" label="Hide revoked" density="compact" hide-details color="primary" />
      </v-col>
    </v-row>

    <v-alert v-if="error" :type="alertType(error.kind)" variant="tonal" class="mb-3">
      <div class="d-flex align-center ga-2">
        <span>{{ error.message }}</span>
        <v-spacer />
        <v-btn size="small" @click="load">Retry</v-btn>
      </div>
    </v-alert>

    <v-progress-linear v-if="loading" indeterminate class="mb-2" />

    <div v-if="!loading && !error && !rows.length" class="text-medium-emphasis pa-4 text-center">
      {{ keys.length ? 'No API keys match the current filters.' : 'No API keys yet. Create one with "New API key".' }}
    </div>

    <div v-if="rows.length">
      <v-card class="table-scroll">
        <v-table density="comfortable">
          <thead>
            <tr>
              <th>Name</th>
              <th>Key</th>
              <th>Permission</th>
              <th>Apps</th>
              <th>Environments</th>
              <th>Status</th>
              <th>Created</th>
              <th>Expires</th>
              <th>Last used</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr v-for="k in rows" :key="k.id">
              <td class="text-break col-name">{{ k.name }}</td>
              <td class="font-mono text-no-wrap">{{ keyDisplay(k.prefix) }}</td>
              <td>
                <v-chip size="small" label :color="PERMISSION_COLOR[k.permission]">{{ k.permission }}</v-chip>
              </td>
              <td>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip v-for="a in scopeChips(k.apps)" :key="a" size="x-small" label class="font-mono">{{ a }}</v-chip>
                </div>
              </td>
              <td>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip v-for="e in scopeChips(k.environments)" :key="e" size="x-small" label class="font-mono">
                    <EnvDot v-if="e !== 'all'" :name="e" :size="6" class="mr-1" />{{ e }}
                  </v-chip>
                </div>
              </td>
              <td>
                <v-chip size="small" label :color="STATUS_COLOR[k.status]" :prepend-icon="STATUS_ICON[k.status]">
                  {{ k.status }}
                </v-chip>
              </td>
              <td class="text-no-wrap">{{ formatInstant(k.createdAt) }}</td>
              <td class="text-no-wrap">{{ formatInstant(k.expiresAt, 'never') }}</td>
              <td class="text-no-wrap">{{ formatInstant(k.lastUsedAt, 'never') }}</td>
              <td class="text-no-wrap text-right">
                <v-btn
                  icon="mdi-autorenew"
                  size="small"
                  variant="text"
                  :disabled="!canChangeKey(k)"
                  :aria-label="`Rotate ${k.name}`"
                  title="Rotate"
                  @click="askConfirm('rotate', k)"
                />
                <v-btn
                  icon="mdi-key-remove"
                  size="small"
                  variant="text"
                  color="error"
                  :disabled="!canChangeKey(k)"
                  :aria-label="`Revoke ${k.name}`"
                  title="Revoke"
                  @click="askConfirm('revoke', k)"
                />
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-card>
      <div class="text-caption text-medium-emphasis mt-2">
        Clients send the key in the <span class="font-mono">X-Api-Key</span> header. Secrets are shown only once, when
        a key is created or rotated.
      </div>
    </div>
    </template>

    <!-- New -->
    <v-dialog v-model="createOpen" max-width="560" scrollable>
      <v-card title="New API key">
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
          <v-text-field
            v-model="expiresModel"
            label="Expires on"
            type="date"
            :min="today"
            clearable
            :error-messages="expiresError ?? undefined"
            hint="Optional. Empty = never expires. The key expires at the end of that day."
            persistent-hint
          />
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

    <!-- Revoke / rotate confirm -->
    <v-dialog v-model="confirm.show" max-width="460">
      <v-card :title="confirm.action === 'revoke' ? 'Revoke API key' : 'Rotate API key'">
        <v-card-text>
          <p v-if="confirm.action === 'revoke'">
            Revoke <strong>{{ confirm.key?.name }}</strong>? Clients using it stop working immediately. This cannot be
            undone.
          </p>
          <p v-else>
            Rotate <strong>{{ confirm.key?.name }}</strong>? A new secret is issued and the current one stops working.
          </p>
          <v-alert v-if="confirm.error" :type="alertType(confirm.error.kind)" variant="tonal" class="mt-3">
            {{ confirm.error.message }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="confirm.busy" @click="confirm.show = false">Cancel</v-btn>
          <v-btn
            :color="confirm.action === 'revoke' ? 'error' : 'warning'"
            variant="flat"
            :loading="confirm.busy"
            @click="runConfirm"
          >
            {{ confirm.action === 'revoke' ? 'Revoke' : 'Rotate' }}
          </v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- One-time secret -->
    <v-dialog v-model="secretOpen" max-width="640" persistent scrollable>
      <v-card :title="issuedAction === 'created' ? 'API key created' : 'API key rotated'">
        <v-card-text v-if="issued">
          <v-alert type="warning" variant="tonal" class="mb-3" title="Shown only once">
            Copy the secret now and store it securely. It cannot be retrieved again; if it is lost, rotate the key.
          </v-alert>
          <div class="text-caption text-medium-emphasis mb-1">Secret for {{ issued.key.name }}</div>
          <v-sheet color="surface-light" rounded="lg" class="d-flex align-center ga-2 pa-2 mb-3">
            <code class="font-mono text-break flex-grow-1 secret">{{ issued.secret }}</code>
            <v-btn size="small" prepend-icon="mdi-content-copy" @click="copy(issued.secret)">Copy</v-btn>
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
.keys-view {
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
