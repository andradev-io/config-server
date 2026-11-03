<script setup lang="ts">
// Access (admin): who has which grants. The server drives the identity provider; roles are never typed here.
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch, type ComponentPublicInstance } from 'vue'
import { useDisplay } from 'vuetify'
import {
  addGrant,
  getAccessInfo,
  listGrants,
  listGroups,
  listUsers,
  removeGrant,
  type AccessInfo,
  type SubjectGrant,
  type SubjectType,
} from '../api/access'
import { listEnvironments } from '../api/environments'
import { FilesApiError, listApps } from '../api/files'
import { ANY } from '../auth/grants'
import EnvChip from '../components/EnvChip.vue'
import EnvDot from '../components/EnvDot.vue'
import PageHeader from '../components/PageHeader.vue'
import PermissionTip from '../components/PermissionTip.vue'
import { alertType } from '../utils/alerts'
import {
  APPLY_NOTICE,
  broadWriteWarning,
  buildGrantRequest,
  coveringGrant,
  emptyGrantForm,
  GRANT_COLOR,
  grantedMessage,
  grantKey,
  grantSummary,
  groupItem,
  isAllScope,
  isDuplicateGrant,
  mergePage,
  PAGE_SIZE,
  providerLabel,
  revokeConsequence,
  revokeDenial,
  revokedMessage,
  rolePreview,
  ROLE_FORMATS,
  sameSubject,
  scopeLabel,
  SEARCH_DEBOUNCE_MS,
  sortGrants,
  sourceLabel,
  subjectNoun,
  userItem,
  type GrantForm,
  type SubjectItem,
} from '../utils/accessAdmin'

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }
const aborted = (c: AbortController) => c.signal.aborted

const SUBJECT_ICON: Record<SubjectType, string> = { group: 'mdi-account-group-outline', user: 'mdi-account-outline' }

const snackbar = ref({ show: false, text: '' })
const notify = (text: string) => (snackbar.value = { show: true, text })

// ---- info: is identity management configured?
const info = ref<AccessInfo | null>(null)
const infoLoading = ref(false)
const infoError = ref<ViewError | null>(null)
const enabled = computed(() => !!info.value?.enabled)

async function loadInfo() {
  infoLoading.value = true
  infoError.value = null
  try {
    info.value = await getAccessInfo()
  } catch (e) {
    infoError.value = toError(e)
    info.value = null
  } finally {
    infoLoading.value = false
  }
}

// ---- subjects (Groups / Users tabs, debounced search, paged)
interface SubjectList {
  search: string | null
  items: SubjectItem[]
  loading: boolean
  loaded: boolean
  hasMore: boolean
  error: ViewError | null
}
const emptyList = (): SubjectList => ({ search: '', items: [], loading: false, loaded: false, hasMore: false, error: null })
const lists = reactive<Record<SubjectType, SubjectList>>({ group: emptyList(), user: emptyList() })
const tab = ref<SubjectType>('group')
const current = computed(() => lists[tab.value])
const ctrls: Partial<Record<SubjectType, AbortController>> = {}
const timers: Partial<Record<SubjectType, ReturnType<typeof setTimeout>>> = {}

async function loadSubjects(type: SubjectType, more = false) {
  const l = lists[type]
  clearTimeout(timers[type])
  ctrls[type]?.abort()
  const c = (ctrls[type] = new AbortController())
  const first = more ? l.items.length : 0
  l.loading = true
  l.error = null
  try {
    const q = { search: l.search ?? '', first, max: PAGE_SIZE }
    const page =
      type === 'user'
        ? (await listUsers(q, { signal: c.signal })).map(userItem)
        : (await listGroups(q, { signal: c.signal })).map(groupItem)
    if (aborted(c)) return
    const merged = mergePage(l.items, page, first)
    l.items = merged.items
    l.hasMore = merged.hasMore
    l.loaded = true
  } catch (e) {
    if (aborted(c)) return
    l.error = toError(e)
    if (!more) l.items = []
    l.hasMore = false
  } finally {
    if (!aborted(c)) l.loading = false
  }
}

function scheduleSearch(type: SubjectType) {
  clearTimeout(timers[type])
  timers[type] = setTimeout(() => void loadSubjects(type), SEARCH_DEBOUNCE_MS)
}
watch(
  () => lists.group.search,
  () => scheduleSearch('group'),
)
watch(
  () => lists.user.search,
  () => scheduleSearch('user'),
)
watch(tab, (t) => {
  if (enabled.value && !lists[t].loaded && !lists[t].loading) void loadSubjects(t)
})

const emptyListText = computed(() => {
  const l = current.value
  const noun = tab.value === 'group' ? 'groups' : 'users'
  return l.search?.trim() ? `No ${noun} match "${l.search.trim()}".` : `No ${noun} found.`
})

// ---- selected subject and its grants
const selected = ref<SubjectItem | null>(null)
const grants = ref<SubjectGrant[]>([])
const grantsLoading = ref(false)
const grantsError = ref<ViewError | null>(null)
const rows = computed(() => sortGrants(grants.value))
const directCount = computed(() => grants.value.filter((g) => !g.inherited).length)
let grantsCtrl: AbortController | undefined

async function loadGrants() {
  const s = selected.value
  grantsCtrl?.abort()
  if (!s) return
  const c = (grantsCtrl = new AbortController())
  grantsLoading.value = true
  grantsError.value = null
  try {
    const list = await listGrants(s.type, s.id, { signal: c.signal })
    if (!aborted(c)) grants.value = list
  } catch (e) {
    if (aborted(c)) return
    grantsError.value = toError(e)
    grants.value = []
  } finally {
    if (!aborted(c)) grantsLoading.value = false
  }
}

const { smAndDown } = useDisplay()
const grantsCard = ref<ComponentPublicInstance | null>(null)
function select(s: SubjectItem) {
  if (sameSubject(s, selected.value)) return
  selected.value = s
  grants.value = []
  void loadGrants()
  // Stacked layout: bring the grants into view.
  if (smAndDown.value)
    void nextTick(() => (grantsCard.value?.$el as HTMLElement | undefined)?.scrollIntoView({ behavior: 'smooth', block: 'start' }))
}

// ---- applications / environments for the grant dialog (best effort: "All" is always offered)
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
const appItems = computed(() => [{ title: 'All applications', value: ANY }, ...apps.value.map((x) => ({ title: x, value: x }))])
const envItems = computed(() => [{ title: 'All environments', value: ANY }, ...envs.value.map((x) => ({ title: x, value: x }))])

// ---- grant dialog
const grantOpen = ref(false)
const form = ref<GrantForm>(emptyGrantForm())
const grantBusy = ref(false)
const grantError = ref<ViewError | null>(null)
// Clearing a field means "all".
const appModel = computed({
  get: () => form.value.app,
  set: (v: string | null) => (form.value.app = v || ANY),
})
const envModel = computed({
  get: () => form.value.environment,
  set: (v: string | null) => (form.value.environment = v || ANY),
})
const preview = computed(() => rolePreview(form.value))
const previewSummary = computed(() => grantSummary(form.value))
const broadWarning = computed(() => broadWriteWarning(form.value))
const duplicate = computed(() => isDuplicateGrant(grants.value, form.value))
const covering = computed(() => (duplicate.value ? null : coveringGrant(grants.value, form.value)))
const canGrant = computed(() => !!selected.value && !duplicate.value && !grantBusy.value)

function openGrant() {
  form.value = emptyGrantForm()
  grantError.value = null
  grantOpen.value = true
  void loadScopes()
}
async function submitGrant() {
  const s = selected.value
  if (!s || !canGrant.value) return
  grantBusy.value = true
  grantError.value = null
  try {
    const g = await addGrant(s.type, s.id, buildGrantRequest(form.value))
    grantOpen.value = false
    notify(grantedMessage(g.role || preview.value, s.type, s.title))
    void loadGrants()
  } catch (e) {
    grantError.value = toError(e)
  } finally {
    grantBusy.value = false
  }
}

// ---- revoke (confirm first)
const revoke = ref<{ show: boolean; grant: SubjectGrant | null; busy: boolean; error: ViewError | null }>({
  show: false,
  grant: null,
  busy: false,
  error: null,
})
function askRevoke(g: SubjectGrant) {
  revoke.value = { show: true, grant: g, busy: false, error: null }
}
async function runRevoke() {
  const r = revoke.value
  const s = selected.value
  if (!r.grant || !s || r.busy) return
  r.busy = true
  r.error = null
  try {
    await removeGrant(s.type, s.id, r.grant)
    notify(revokedMessage(r.grant.role, s.type, s.title))
    r.show = false
    void loadGrants()
  } catch (e) {
    r.error = toError(e)
  } finally {
    r.busy = false
  }
}

// ---- lifecycle
async function refresh() {
  await loadInfo()
  if (!enabled.value) return
  void loadSubjects(tab.value)
  void loadGrants()
  void loadScopes()
}
onMounted(() => void refresh())
onBeforeUnmount(() => {
  for (const t of Object.values(timers)) clearTimeout(t)
  for (const c of Object.values(ctrls)) c?.abort()
  grantsCtrl?.abort()
})

const subtitle = computed(() => {
  if (!info.value?.enabled) return 'Grant users and groups access to applications and environments.'
  const client = info.value.configClientId ? ` (client ${info.value.configClientId})` : ''
  return `Grant users and groups access to applications and environments, through ${providerLabel(info.value.provider)}${client}.`
})
</script>

<template>
  <div class="access-view">
    <PageHeader title="Access" icon="mdi-account-key-outline" :subtitle="subtitle">
      <template #actions>
        <v-btn prepend-icon="mdi-refresh" :loading="infoLoading" @click="refresh">Refresh</v-btn>
      </template>
    </PageHeader>

    <v-progress-linear v-if="infoLoading && !info" indeterminate class="mb-2" />

    <v-alert v-if="infoError" :type="alertType(infoError.kind)" variant="tonal" class="mb-3">
      <div class="d-flex align-center ga-2">
        <span>{{ infoError.message }}</span>
        <v-spacer />
        <v-btn size="small" @click="refresh">Retry</v-btn>
      </div>
    </v-alert>

    <!-- Identity management not configured: grants are assigned in the IdP directly -->
    <v-card v-if="info && !info.enabled" class="pa-6 not-configured">
      <div class="text-center">
        <v-icon icon="mdi-shield-account-outline" size="36" class="mb-2 text-medium-emphasis" />
        <div class="text-subtitle-1 mb-1">Identity management is not configured</div>
        <div class="text-body-2 text-medium-emphasis">
          This server does not manage the identity provider (provider: {{ providerLabel(info.provider) }}). Assign access
          in the identity provider directly, by giving users or groups these client roles:
        </div>
        <div v-if="info.note" class="text-body-2 text-medium-emphasis mt-2">{{ info.note }}</div>
      </div>
      <v-table density="compact" class="role-formats mt-4">
        <tbody>
          <tr v-for="r in ROLE_FORMATS" :key="r.role">
            <td class="font-mono text-no-wrap">{{ r.role }}</td>
            <td class="text-medium-emphasis">{{ r.meaning }}</td>
          </tr>
        </tbody>
      </v-table>
      <div class="text-caption text-medium-emphasis mt-4 text-center">
        Write implies read; a missing segment means all. To manage grants from this screen, configure the
        <span class="font-mono">IdentityAdmin</span> section on the server (see
        <span class="font-mono">docs/keycloak-setup.md</span>).
      </div>
    </v-card>

    <template v-else-if="enabled">
      <v-alert type="info" variant="tonal" density="compact" icon="mdi-clock-outline" class="mb-4">
        {{ APPLY_NOTICE }}.
      </v-alert>
      <v-alert v-if="info?.note" type="info" variant="tonal" density="compact" class="mb-4">{{ info.note }}</v-alert>

      <v-row dense>
        <!-- Subjects -->
        <v-col cols="12" md="5" lg="4">
          <v-card>
            <v-tabs v-model="tab" grow density="comfortable" aria-label="Subject type">
              <v-tab value="group" :prepend-icon="SUBJECT_ICON.group">Groups</v-tab>
              <v-tab value="user" :prepend-icon="SUBJECT_ICON.user">Users</v-tab>
            </v-tabs>
            <v-divider />
            <div class="pa-3">
              <v-text-field
                v-model="lists[tab].search"
                :label="tab === 'group' ? 'Search groups' : 'Search users'"
                prepend-inner-icon="mdi-magnify"
                clearable
                density="comfortable"
                hide-details
                autocomplete="off"
              />
              <div v-if="tab === 'group'" class="text-caption text-medium-emphasis mt-2">
                Recommended: grant access to groups and manage membership in the identity provider.
              </div>
            </div>
            <v-progress-linear v-if="current.loading" indeterminate />
            <v-alert v-if="current.error" :type="alertType(current.error.kind)" variant="tonal" density="compact" class="mx-3 mb-3">
              {{ current.error.message }}
              <template #append><v-btn size="x-small" @click="loadSubjects(tab)">Retry</v-btn></template>
            </v-alert>
            <v-list
              v-if="current.items.length"
              density="compact"
              nav
              color="primary"
              class="subject-list"
              :aria-label="tab === 'group' ? 'Groups' : 'Users'"
            >
              <v-list-item
                v-for="s in current.items"
                :key="s.id"
                :value="s.id"
                :active="sameSubject(s, selected)"
                :prepend-icon="SUBJECT_ICON[s.type]"
                :title="s.title"
                :subtitle="s.subtitle || undefined"
                rounded="lg"
                @click="select(s)"
              />
            </v-list>
            <div
              v-else-if="current.loaded && !current.loading && !current.error"
              class="text-body-2 text-medium-emphasis pa-4 text-center"
            >
              {{ emptyListText }}
            </div>
            <div v-if="current.hasMore" class="pa-2 text-center">
              <v-btn size="small" :loading="current.loading" @click="loadSubjects(tab, true)">Load more</v-btn>
            </div>
          </v-card>
        </v-col>

        <!-- Grants of the selected subject -->
        <v-col cols="12" md="7" lg="8">
          <v-card ref="grantsCard">
            <div v-if="!selected" class="pa-6 text-center">
              <v-icon icon="mdi-account-search-outline" size="36" class="mb-2 text-medium-emphasis" />
              <div class="text-subtitle-1 mb-1">No group or user selected</div>
              <div class="text-body-2 text-medium-emphasis">Select one to see its access and grant more.</div>
            </div>

            <template v-else>
              <div class="d-flex flex-wrap align-center ga-3 pa-4">
                <v-icon :icon="SUBJECT_ICON[selected.type]" class="text-medium-emphasis" />
                <div class="subject-head">
                  <div class="text-subtitle-1 text-break">{{ selected.title }}</div>
                  <div class="text-caption text-medium-emphasis text-break">
                    {{ subjectNoun(selected.type) }}<template v-if="selected.subtitle"> · {{ selected.subtitle }}</template>
                  </div>
                </div>
                <v-spacer />
                <v-btn color="primary" variant="flat" prepend-icon="mdi-plus" @click="openGrant">Grant access</v-btn>
              </div>
              <v-divider />
              <v-progress-linear v-if="grantsLoading" indeterminate />
              <v-alert v-if="grantsError" :type="alertType(grantsError.kind)" variant="tonal" class="ma-3">
                <div class="d-flex align-center ga-2">
                  <span>{{ grantsError.message }}</span>
                  <v-spacer />
                  <v-btn size="small" @click="loadGrants">Retry</v-btn>
                </div>
              </v-alert>
              <div
                v-if="!grantsLoading && !grantsError && !rows.length"
                class="text-body-2 text-medium-emphasis pa-6 text-center"
              >
                No access yet. Use "Grant access" to give this {{ subjectNoun(selected.type) }} read or write on an
                application.
              </div>
              <div v-if="rows.length" class="table-scroll">
                <v-table density="comfortable">
                  <thead>
                    <tr>
                      <th>Permission</th>
                      <th>Application</th>
                      <th>Environment</th>
                      <th>Source</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="g in rows" :key="grantKey(g)">
                      <td>
                        <v-chip size="small" label :color="GRANT_COLOR[g.permission]" :title="`${g.role}: ${grantSummary(g)}`">
                          {{ g.permission }}
                        </v-chip>
                      </td>
                      <td>
                        <span v-if="!isAllScope(g.app)" class="font-mono text-break">{{ g.app }}</span>
                        <v-chip v-else size="small" label class="font-mono">all</v-chip>
                      </td>
                      <td>
                        <EnvChip v-if="!isAllScope(g.environment)" :name="g.environment" />
                        <v-chip v-else size="small" label class="font-mono">{{ scopeLabel(g.environment) }}</v-chip>
                      </td>
                      <td class="text-no-wrap">
                        <span class="d-inline-flex align-center ga-1" :class="{ 'text-medium-emphasis': g.inherited }">
                          <v-icon
                            :icon="g.inherited ? 'mdi-account-group-outline' : 'mdi-link-variant'"
                            size="16"
                            aria-hidden="true"
                          />
                          {{ sourceLabel(g) }}
                        </span>
                      </td>
                      <td class="text-no-wrap text-right">
                        <PermissionTip :reason="revokeDenial(g, selected.type)" location="start">
                          <v-btn
                            icon="mdi-shield-remove-outline"
                            size="small"
                            variant="text"
                            color="error"
                            :disabled="!!revokeDenial(g, selected.type)"
                            :aria-label="`Revoke ${g.role}`"
                            title="Revoke"
                            @click="askRevoke(g)"
                          />
                        </PermissionTip>
                      </td>
                    </tr>
                  </tbody>
                </v-table>
              </div>
              <div v-if="rows.length && rows.length !== directCount" class="text-caption text-medium-emphasis px-4 pb-3">
                Inherited grants come from groups and are changed on the group.
              </div>
            </template>
          </v-card>
        </v-col>
      </v-row>
    </template>

    <!-- Grant access -->
    <v-dialog v-model="grantOpen" max-width="560" scrollable>
      <v-card :title="`Grant access to ${selected?.title ?? ''}`">
        <v-card-text>
          <v-radio-group v-model="form.permission" label="Permission" inline density="compact" hide-details class="mb-3">
            <v-radio value="read" label="read" />
            <v-radio value="write" label="write (includes read)" />
          </v-radio-group>
          <v-autocomplete
            v-model="appModel"
            :items="appItems"
            label="Application"
            prepend-inner-icon="mdi-cube-outline"
            no-data-text="No applications"
            hint="One application, or all of them (including future ones)."
            persistent-hint
            class="mb-3"
          />
          <v-select
            v-model="envModel"
            :items="envItems"
            label="Environment"
            no-data-text="No environments"
            hint="One environment, or all of them (including future ones)."
            persistent-hint
            class="mb-3"
          >
            <template #selection="{ item }">
              <span class="d-inline-flex align-center ga-2 font-mono text-truncate">
                <EnvDot v-if="item.value !== ANY" :name="String(item.value)" />{{ item.title }}
              </span>
            </template>
            <template #item="{ props: itemProps, item }">
              <v-list-item v-bind="itemProps">
                <template v-if="item.value !== ANY" #prepend><EnvDot :name="String(item.value)" class="mr-3" /></template>
              </v-list-item>
            </template>
          </v-select>

          <v-sheet color="surface-light" rounded="lg" class="pa-3" aria-live="polite">
            <div class="text-body-2">{{ previewSummary }}</div>
            <div class="text-caption text-medium-emphasis mt-1">
              Role <span class="font-mono">{{ preview }}</span>
            </div>
          </v-sheet>

          <v-alert v-if="broadWarning" type="warning" variant="tonal" density="compact" class="mt-3">
            {{ broadWarning }}
          </v-alert>
          <v-alert v-if="duplicate" type="info" variant="tonal" density="compact" class="mt-3">
            This {{ selected ? subjectNoun(selected.type) : 'subject' }} already has this grant.
          </v-alert>
          <v-alert v-else-if="covering" type="info" variant="tonal" density="compact" class="mt-3">
            Already covered by <span class="font-mono">{{ covering.role }}</span> ({{ sourceLabel(covering) }}). The new
            grant only matters if that one is removed.
          </v-alert>
          <v-alert v-if="grantError" :type="alertType(grantError.kind)" variant="tonal" class="mt-3">
            {{ grantError.message }}
          </v-alert>
          <div class="text-caption text-medium-emphasis mt-3">{{ APPLY_NOTICE }}.</div>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="grantBusy" @click="grantOpen = false">Cancel</v-btn>
          <v-btn color="primary" variant="flat" :disabled="!canGrant" :loading="grantBusy" @click="submitGrant">Grant</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Revoke confirm -->
    <v-dialog v-model="revoke.show" max-width="460">
      <v-card title="Revoke access">
        <v-card-text>
          <p v-if="revoke.grant && selected">
            Revoke <span class="font-mono">{{ revoke.grant.role }}</span> from {{ subjectNoun(selected.type) }}
            <strong>{{ selected.title }}</strong>? {{ revokeConsequence(revoke.grant, selected.type) }}
          </p>
          <div class="text-caption text-medium-emphasis mt-2">{{ APPLY_NOTICE }}.</div>
          <v-alert v-if="revoke.error" :type="alertType(revoke.error.kind)" variant="tonal" class="mt-3">
            {{ revoke.error.message }}
          </v-alert>
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn :disabled="revoke.busy" @click="revoke.show = false">Cancel</v-btn>
          <v-btn color="error" variant="flat" :loading="revoke.busy" @click="runRevoke">Revoke</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <v-snackbar v-model="snackbar.show" timeout="4000">{{ snackbar.text }}</v-snackbar>
  </div>
</template>

<style scoped>
.access-view {
  min-width: 0;
}
.table-scroll {
  overflow-x: auto;
}
.subject-list {
  max-height: 60vh;
  overflow-y: auto;
}
.subject-head {
  min-width: 0;
  flex: 1 1 160px;
}
.not-configured {
  max-width: 760px;
  margin: 0 auto;
}
.role-formats {
  background: transparent;
}
</style>
