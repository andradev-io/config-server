<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  createEnvironment,
  deleteEnvironment,
  listEnvironments,
  updateEnvironment,
  type EnvironmentInfo,
} from '../api/environments'
import { FilesApiError } from '../api/files'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import EnvChip from '../components/EnvChip.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import PermissionTip from '../components/PermissionTip.vue'
import { environmentsWriteDenial, hasNoAccess } from '../utils/access'
import { ENV_NAME_HINT, validateEnvName } from '../utils/variables'

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const alertType = (kind: string) => (kind === 'not-found' || kind === 'forbidden' || kind === 'read-only' ? 'warning' : 'error')
// Environments are global: create / edit / delete need write on every application and environment.
const { grants } = useAuth()
const writeDenial = computed(() => environmentsWriteDenial(grants.value))
const canWrite = computed(() => writeDenial.value === null)
const noAccess = computed(() => hasNoAccess(grants.value))

const envs = ref<EnvironmentInfo[]>([])
const loading = ref(false)
const error = ref<ViewError | null>(null)
// Applications come from the global context; it is refreshed after changes so the app bar stays current.
const ctx = useContext()
const apps = computed(() => ctx.state.apps)
const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

const sorted = computed(() => [...envs.value].sort((a, b) => a.order - b.order || a.name.localeCompare(b.name)))

async function load() {
  loading.value = true
  error.value = null
  try {
    envs.value = await listEnvironments()
  } catch (e) {
    error.value = toError(e)
    envs.value = []
  } finally {
    loading.value = false
  }
}
const afterChange = () => {
  void load()
  void ctx.refresh()
}
onMounted(() => void load())

// ---- create
const createOpen = ref(false)
const name = ref('')
const description = ref('')
const order = ref<string>('')
const scaffoldApps = ref<string[]>([])
const copyFrom = ref<string | null>(null)
const createBusy = ref(false)
const createError = ref<ViewError | null>(null)

const nameError = computed(() => {
  if (!name.value) return null
  const e = validateEnvName(name.value)
  if (e) return e
  return envs.value.some((x) => x.name.toLowerCase() === name.value.toLowerCase()) ? 'Environment already exists' : null
})
const orderError = computed(() => (order.value !== '' && !Number.isInteger(Number(order.value)) ? 'Whole number' : null))
const canCreate = computed(() => !!name.value && !nameError.value && !orderError.value && !createBusy.value)

function openCreate() {
  name.value = ''
  description.value = ''
  order.value = ''
  scaffoldApps.value = []
  copyFrom.value = null
  createError.value = null
  createOpen.value = true
}
async function create() {
  if (!canCreate.value) return
  createBusy.value = true
  createError.value = null
  try {
    await createEnvironment({
      name: name.value,
      description: description.value || undefined,
      order: order.value === '' ? undefined : Number(order.value),
      scaffold: scaffoldApps.value.length
        ? { apps: scaffoldApps.value, copyFrom: copyFrom.value || undefined }
        : undefined,
    })
    createOpen.value = false
    notify(`Environment ${name.value} created`)
    afterChange()
  } catch (e) {
    createError.value = toError(e)
  } finally {
    createBusy.value = false
  }
}

// ---- edit
const editOpen = ref(false)
const editName = ref('')
const editDescription = ref('')
const editOrder = ref('0')
const editBusy = ref(false)
const editError = ref<ViewError | null>(null)
const editOrderError = computed(() => (!Number.isInteger(Number(editOrder.value)) || editOrder.value === '' ? 'Whole number' : null))

function openEdit(e: EnvironmentInfo) {
  editName.value = e.name
  editDescription.value = e.description
  editOrder.value = String(e.order)
  editError.value = null
  editOpen.value = true
}
async function saveEdit() {
  if (editOrderError.value || editBusy.value) return
  editBusy.value = true
  editError.value = null
  try {
    await updateEnvironment(editName.value, { description: editDescription.value, order: Number(editOrder.value) })
    editOpen.value = false
    notify(`Environment ${editName.value} updated`)
    afterChange()
  } catch (e) {
    editError.value = toError(e)
  } finally {
    editBusy.value = false
  }
}

// ---- delete
const deleteOpen = ref(false)
const deleteTarget = ref<EnvironmentInfo | null>(null)
const deleteFiles = ref(false)
const deleteBusy = ref(false)
const deleteError = ref<ViewError | null>(null)

function openDelete(e: EnvironmentInfo) {
  deleteTarget.value = e
  deleteFiles.value = false
  deleteError.value = null
  deleteOpen.value = true
}
async function confirmDelete() {
  if (!deleteTarget.value) return
  deleteBusy.value = true
  deleteError.value = null
  try {
    await deleteEnvironment(deleteTarget.value.name, deleteFiles.value)
    deleteOpen.value = false
    notify(`Environment ${deleteTarget.value.name} deleted`)
    afterChange()
  } catch (e) {
    deleteError.value = toError(e)
  } finally {
    deleteBusy.value = false
  }
}
</script>

<template>
  <div class="environments-view">
    <PageHeader
      title="Environments"
      icon="mdi-layers-outline"
      subtitle="Declared environments and the ones implied by override files, in order."
    >
      <template #actions>
        <v-btn prepend-icon="mdi-refresh" :loading="loading" @click="load">Refresh</v-btn>
        <PermissionTip :reason="writeDenial">
          <v-btn color="primary" variant="flat" prepend-icon="mdi-plus" :disabled="!canWrite" @click="openCreate">
            New environment
          </v-btn>
        </PermissionTip>
      </template>
    </PageHeader>

    <!-- Signed in without any grant: the empty state instead of errors -->
    <NoAppState v-if="noAccess" />

    <v-alert v-if="error && !noAccess" :type="alertType(error.kind)" variant="tonal" class="mb-3">
      <div class="d-flex align-center ga-2">
        <span>{{ error.message }}</span>
        <v-spacer />
        <v-btn size="small" @click="load">Retry</v-btn>
      </div>
    </v-alert>

    <v-progress-linear v-if="loading" indeterminate class="mb-2" />

    <div v-if="!noAccess && !loading && !error && !sorted.length" class="text-medium-emphasis pa-4 text-center">
      No environments yet.<template v-if="canWrite"> Create one with "New environment".</template>
    </div>

    <div v-if="!noAccess && sorted.length">
      <v-card class="table-scroll">
        <v-table density="comfortable">
          <thead>
            <tr>
              <th>Name</th>
              <th>Description</th>
              <th class="text-right">Order</th>
              <th class="text-right">Apps</th>
              <th>Kind</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in sorted" :key="e.name">
              <td><EnvChip :name="e.name" /></td>
              <td class="text-break">{{ e.description }}</td>
              <td class="text-right">{{ e.order }}</td>
              <td class="text-right">{{ e.appCount }}</td>
              <td>
                <v-chip
                  size="small"
                  label
                  :color="e.declared ? 'neutral' : 'muted'"
                  :prepend-icon="e.declared ? 'mdi-check-decagram-outline' : 'mdi-eye-off-outline'"
                >
                  {{ e.declared ? 'declared' : 'implicit' }}
                </v-chip>
              </td>
              <td class="text-no-wrap text-right">
                <PermissionTip :reason="writeDenial" location="start">
                  <v-btn
                    icon="mdi-pencil-outline"
                    size="small"
                    variant="text"
                    :aria-label="`Edit ${e.name}`"
                    :disabled="!canWrite"
                    @click="openEdit(e)"
                  />
                  <v-btn
                    icon="mdi-delete-outline"
                    size="small"
                    variant="text"
                    color="error"
                    :aria-label="`Delete ${e.name}`"
                    :disabled="!canWrite"
                    @click="openDelete(e)"
                  />
                </PermissionTip>
              </td>
            </tr>
          </tbody>
        </v-table>
      </v-card>
      <div class="text-caption text-medium-emphasis mt-2">
        Implicit environments exist only as override files (no entry in the environments declaration).
      </div>
    </div>

    <!-- New -->
    <v-dialog v-model="createOpen" max-width="520" scrollable>
      <v-card title="New environment">
        <v-card-text>
          <v-text-field
            v-model="name"
            label="Name"
            autocomplete="off"
            autofocus
            :error-messages="nameError ?? undefined"
            :hint="ENV_NAME_HINT"
            persistent-hint
            class="mb-3"
          />
          <v-text-field v-model="description" label="Description" autocomplete="off" class="mb-1" />
          <v-text-field
            v-model="order"
            label="Order"
            type="number"
            autocomplete="off"
            :error-messages="orderError ?? undefined"
            hint="Optional. Lower values come first."
            persistent-hint
            class="mb-3"
          />
          <v-select
            v-model="scaffoldApps"
            :items="apps"
            label="Create override files in applications"
            multiple
            chips
            closable-chips
            no-data-text="No applications"
            hint="Optional. Scaffolds an override file for this environment in each selected app."
            persistent-hint
            class="mb-3"
          />
          <v-select
            v-model="copyFrom"
            :items="sorted.map((x) => x.name)"
            label="Copy from environment"
            clearable
            :disabled="!scaffoldApps.length"
            hint="Optional. Start the new files from this environment's files."
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

    <!-- Edit -->
    <v-dialog v-model="editOpen" max-width="480">
      <v-card :title="`Edit ${editName}`">
        <v-card-text>
          <v-text-field v-model="editDescription" label="Description" autocomplete="off" class="mb-1" autofocus />
          <v-text-field
            v-model="editOrder"
            label="Order"
            type="number"
            autocomplete="off"
            :error-messages="editOrderError ?? undefined"
          />
          <v-alert v-if="editError" :type="alertType(editError.kind)" variant="tonal" class="mt-3">
            {{ editError.message }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="editOpen = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" :disabled="!!editOrderError" :loading="editBusy" @click="saveEdit">Save</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Delete -->
    <v-dialog v-model="deleteOpen" max-width="460">
      <v-card title="Delete environment">
        <v-card-text>
          <p>
            Delete <strong>{{ deleteTarget?.name }}</strong>?
            <template v-if="deleteTarget && !deleteTarget.declared">
              It is implicit (it exists only as override files), so the files must be deleted too.
            </template>
          </p>
          <v-checkbox
            v-model="deleteFiles"
            label="Also delete the override files in all apps"
            density="compact"
            hide-details
            color="error"
          />
          <v-alert v-if="deleteError" :type="alertType(deleteError.kind)" variant="tonal" class="mt-3">
            {{ deleteError.message }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="deleteOpen = false">Cancel</v-btn>
          <v-btn color="error" variant="flat" :loading="deleteBusy" @click="confirmDelete">Delete</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" timeout="2500">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.environments-view {
  min-width: 0;
}
.table-scroll {
  overflow-x: auto;
}
</style>
