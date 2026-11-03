<script setup lang="ts">
// Default screen. Without an application: the app list as cards. With one: per-environment status
// cards (GET api/apps/{app}/status) with quick actions into Overrides, Editor and the merged config.
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { FilesApiError, listFiles } from '../api/files'
import { getAppStatus, type AppStatus } from '../api/variables'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import EnvChip from '../components/EnvChip.vue'
import NoAppState from '../components/NoAppState.vue'
import PageHeader from '../components/PageHeader.vue'
import PermissionTip from '../components/PermissionTip.vue'
import { hasNoAccess, openEnvFileDenial, variablesWriteDenial } from '../utils/access'
import { alertType } from '../utils/alerts'
import { envColorCss } from '../utils/envColor'
import type { ViewId } from '../utils/nav'
import { HEALTH_VISUAL, orderEnvironments, overrideFiles, summarizeApp, summarizeEnv } from '../utils/overview'

const props = defineProps<{ active: boolean }>()
const emit = defineEmits<{ open: [view: ViewId, env?: string] }>()
const ctx = useContext()
// Quick actions follow the grants: reading is implied by what the server lists, writing is checked.
const { grants } = useAuth()
const noAccess = computed(() => hasNoAccess(grants.value))

interface ViewError {
  kind: string
  message: string
}
const toError = (e: unknown): ViewError =>
  e instanceof FilesApiError ? { kind: e.kind, message: e.message } : { kind: 'network', message: 'Unexpected error.' }

const app = computed(() => ctx.state.app)
const envOrder = computed(() => ctx.state.environments.map((e) => e.name))

// ---- application cards (no app selected)
interface AppCard {
  name: string
  envs: string[] | null
  failed: boolean
}
const cards = ref<AppCard[]>([])
const cardsLoading = ref(false)
let cardsSeq = 0
async function loadCards() {
  const n = ++cardsSeq
  const apps = [...ctx.state.apps]
  cards.value = apps.map((name) => ({ name, envs: null, failed: false }))
  if (!apps.length) return
  cardsLoading.value = true
  const results = await Promise.allSettled(apps.map((a) => listFiles(a)))
  if (n !== cardsSeq) return
  cards.value = apps.map((name, i) => {
    const r = results[i]
    return r.status === 'fulfilled'
      ? { name, envs: orderEnvironments([...overrideFiles(r.value.map((f) => f.name)).keys()], envOrder.value), failed: false }
      : { name, envs: null, failed: true }
  })
  cardsLoading.value = false
}

// ---- status of the selected app
const status = ref<AppStatus | null>(null)
const files = ref<Map<string, string>>(new Map())
const loading = ref(false)
const error = ref<ViewError | null>(null)
let ctrl: AbortController | undefined
async function loadStatus() {
  const a = app.value
  ctrl?.abort()
  if (!a) return
  const c = (ctrl = new AbortController())
  loading.value = true
  error.value = null
  const [s, f] = await Promise.allSettled([getAppStatus(a, { signal: c.signal }), listFiles(a, { signal: c.signal })])
  if (c.signal.aborted) return
  if (s.status === 'fulfilled') status.value = s.value
  else {
    status.value = null
    error.value = toError(s.reason)
  }
  files.value = f.status === 'fulfilled' ? overrideFiles(f.value.map((x) => x.name)) : new Map()
  loading.value = false
}

const load = () => (app.value ? loadStatus() : loadCards())

// Refresh whenever the screen is shown or the context changes while it is visible.
let stale = true
watch(
  () => [app.value, ctx.state.apps.length, ctx.state.loaded] as const,
  (_, before) => {
    if (before && before[0] !== app.value) {
      status.value = null
      error.value = null
    }
    if (props.active && ctx.state.loaded) void load()
    else stale = true
  },
)
watch(
  () => props.active,
  (a) => {
    if (a && ctx.state.loaded && stale) {
      stale = false
      void load()
    } else if (!a) stale = true
  },
  { immediate: true },
)
onBeforeUnmount(() => ctrl?.abort())

const envCards = computed(() => {
  if (!status.value) return []
  const a = app.value ?? ''
  const byName = new Map(status.value.environments.map((e) => [e.name, e]))
  return orderEnvironments([...byName.keys()], envOrder.value).map((name) => {
    const e = byName.get(name)!
    const file = files.value.get(name) ?? null
    return { status: e, summary: summarizeEnv(e), file, editorDenial: openEnvFileDenial(grants.value, a, name, !!file) }
  })
})
const summary = computed(() => (status.value ? summarizeApp(status.value) : null))
const declareDenial = computed(() => (app.value ? variablesWriteDenial(grants.value, app.value) : null))
const MAX_KEYS = 6
</script>

<template>
  <div class="overview-view">
    <!-- Signed in without any grant: nothing to list (instead of errors) -->
    <template v-if="noAccess">
      <PageHeader title="Applications" icon="mdi-view-dashboard-outline" />
      <NoAppState />
    </template>

    <!-- No application: pick one -->
    <template v-else-if="!app">
      <PageHeader title="Applications" icon="mdi-view-dashboard-outline" subtitle="Pick an application to see its status per environment.">
        <template #actions>
          <v-btn prepend-icon="mdi-refresh" :loading="cardsLoading || ctx.state.loading" @click="ctx.refresh().then(loadCards)">
            Refresh
          </v-btn>
        </template>
      </PageHeader>
      <v-alert v-if="ctx.state.error" type="error" variant="tonal" class="mb-4">{{ ctx.state.error }}</v-alert>
      <div v-if="ctx.state.loaded && !cards.length" class="text-medium-emphasis pa-6 text-center">No applications found.</div>
      <v-row dense>
        <v-col v-for="c in cards" :key="c.name" cols="12" sm="6" lg="4" xl="3">
          <v-card class="app-card" link :aria-label="`Open ${c.name}`" @click="ctx.requestApp(c.name)">
            <v-card-item>
              <template #prepend>
                <v-avatar color="surface-light" rounded="lg" size="40">
                  <v-icon icon="mdi-cube-outline" />
                </v-avatar>
              </template>
              <v-card-title class="font-mono text-break app-card__title">{{ c.name }}</v-card-title>
              <v-card-subtitle>
                <template v-if="c.failed">Files unavailable</template>
                <template v-else-if="c.envs === null">Loading...</template>
                <template v-else>{{ c.envs.length }} environment{{ c.envs.length === 1 ? '' : 's' }}</template>
              </v-card-subtitle>
            </v-card-item>
            <v-card-text v-if="c.envs && c.envs.length" class="pt-0">
              <div class="d-flex flex-wrap ga-1">
                <EnvChip v-for="e in c.envs" :key="e" :name="e" size="x-small" />
              </div>
            </v-card-text>
          </v-card>
        </v-col>
      </v-row>
    </template>

    <!-- Application selected: status per environment -->
    <template v-else>
      <PageHeader :title="app" icon="mdi-cube-outline">
        <template #subtitle>
          <template v-if="summary">
            <span>{{ summary.environments }} environment{{ summary.environments === 1 ? '' : 's' }}</span>
            <span aria-hidden="true">·</span>
            <span :class="summary.ok === summary.environments ? 'text-success' : ''">{{ summary.ok }} ok</span>
            <template v-if="summary.warning"><span aria-hidden="true">·</span><span class="text-warning">{{ summary.warning }} with issues</span></template>
            <template v-if="summary.error"><span aria-hidden="true">·</span><span class="text-error">{{ summary.error }} failing</span></template>
            <template v-if="summary.undeclared"><span aria-hidden="true">·</span><span>{{ summary.undeclared }} undeclared</span></template>
          </template>
          <span v-else>Status per environment</span>
        </template>
        <template #actions>
          <v-btn prepend-icon="mdi-arrow-left" @click="ctx.requestApp(null)">All applications</v-btn>
          <v-btn prepend-icon="mdi-refresh" :loading="loading" @click="loadStatus">Refresh</v-btn>
          <v-btn color="primary" variant="flat" prepend-icon="mdi-table-edit" @click="emit('open', 'overrides')">Open overrides</v-btn>
        </template>
      </PageHeader>

      <v-alert v-if="error" :type="alertType(error.kind)" variant="tonal" class="mb-4">
        <div class="d-flex align-center flex-wrap ga-2">
          <span>{{ error.message }}</span>
          <v-spacer />
          <v-btn size="small" @click="loadStatus">Retry</v-btn>
        </div>
      </v-alert>
      <v-progress-linear v-if="loading && !status" indeterminate class="mb-4" />

      <div v-if="status && !envCards.length" class="text-medium-emphasis pa-6 text-center">
        No environments to check for this application.
      </div>

      <v-row v-if="envCards.length">
        <v-col v-for="c in envCards" :key="c.summary.name" cols="12" md="6" xl="4">
          <v-card class="env-card h-100 d-flex flex-column" :style="{ '--env-color': envColorCss(c.summary.name) }">
            <v-card-item>
              <div class="d-flex align-center flex-wrap ga-2">
                <EnvChip :name="c.summary.name" />
                <span v-if="ctx.state.env === c.summary.name" class="text-caption text-medium-emphasis">current</span>
                <v-spacer />
                <v-chip
                  size="small"
                  label
                  :color="HEALTH_VISUAL[c.summary.level].color"
                  :prepend-icon="HEALTH_VISUAL[c.summary.level].icon"
                >
                  {{ c.summary.label }}
                </v-chip>
              </div>
              <div class="text-caption text-medium-emphasis mt-2 font-mono">
                <template v-if="c.file">
                  <v-icon icon="mdi-file-outline" size="x-small" class="mr-1" />{{ c.file }}
                </template>
                <span v-else class="text-warning">
                  <v-icon icon="mdi-file-alert-outline" size="x-small" class="mr-1" />no override file (merged config returns 404)
                </span>
              </div>
            </v-card-item>

            <v-card-text class="flex-grow-1 pt-0">
              <v-alert v-if="c.status.error" type="error" variant="tonal" density="compact" class="mb-3 text-break">
                {{ c.status.error }}
              </v-alert>
              <dl class="metrics">
                <div class="metric" :class="{ bad: c.summary.missing }">
                  <dt><v-icon icon="mdi-asterisk" size="x-small" class="mr-1" />Missing required</dt>
                  <dd>{{ c.summary.missing }}</dd>
                </div>
                <div class="metric" :class="{ bad: c.summary.typeMismatches }">
                  <dt><v-icon icon="mdi-swap-horizontal" size="x-small" class="mr-1" />Type mismatches</dt>
                  <dd>{{ c.summary.typeMismatches }}</dd>
                </div>
                <div class="metric" :class="{ bad: c.summary.secretsInPlainText }">
                  <dt><v-icon icon="mdi-lock-open-alert-outline" size="x-small" class="mr-1" />Secrets in plain text</dt>
                  <dd>{{ c.summary.secretsInPlainText }}</dd>
                </div>
              </dl>
              <div v-if="c.status.missing.length" class="mt-3">
                <div class="text-caption text-medium-emphasis mb-1">Missing</div>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip v-for="k in c.status.missing.slice(0, MAX_KEYS)" :key="k" size="x-small" label color="error" class="font-mono">{{ k }}</v-chip>
                  <span v-if="c.status.missing.length > MAX_KEYS" class="text-caption text-medium-emphasis">
                    +{{ c.status.missing.length - MAX_KEYS }} more
                  </span>
                </div>
              </div>
              <div v-if="c.status.typeMismatches.length" class="mt-3">
                <div class="text-caption text-medium-emphasis mb-1">Type mismatches</div>
                <div v-for="m in c.status.typeMismatches.slice(0, MAX_KEYS)" :key="m.key" class="font-mono text-caption text-break">
                  {{ m.key }}: expected {{ m.expected }}, got {{ m.actual }}
                </div>
              </div>
              <div v-if="c.status.secretsInPlainText.length" class="mt-3">
                <div class="text-caption text-medium-emphasis mb-1">Secrets in plain text</div>
                <div class="d-flex flex-wrap ga-1">
                  <v-chip
                    v-for="k in c.status.secretsInPlainText.slice(0, MAX_KEYS)"
                    :key="k"
                    size="x-small"
                    label
                    color="warning"
                    class="font-mono"
                  >
                    {{ k }}
                  </v-chip>
                </div>
              </div>
            </v-card-text>

            <v-card-actions class="flex-wrap ga-1 px-4 pb-3">
              <v-btn size="small" variant="text" prepend-icon="mdi-table-edit" @click="emit('open', 'overrides', c.summary.name)">
                Overrides
              </v-btn>
              <PermissionTip :reason="c.editorDenial">
                <v-btn
                  size="small"
                  variant="text"
                  prepend-icon="mdi-file-code-outline"
                  :disabled="!!c.editorDenial"
                  @click="emit('open', 'editor', c.summary.name)"
                >
                  Editor
                </v-btn>
              </PermissionTip>
              <v-btn
                size="small"
                variant="text"
                prepend-icon="mdi-file-eye-outline"
                :disabled="!c.file"
                @click="emit('open', 'view', c.summary.name)"
              >
                Merged config
              </v-btn>
            </v-card-actions>
          </v-card>
        </v-col>
      </v-row>

      <v-card v-if="status && status.undeclared.length" class="mt-4">
        <v-card-item>
          <v-card-title class="text-subtitle-1">
            <v-icon icon="mdi-help-circle-outline" color="warning" size="small" class="mr-2" />
            Undeclared variables ({{ status.undeclared.length }})
          </v-card-title>
          <v-card-subtitle>Keys found in the config files but not declared in Editor &gt; Variables.</v-card-subtitle>
        </v-card-item>
        <v-card-text>
          <div class="d-flex flex-wrap ga-1 mb-3">
            <v-chip v-for="k in status.undeclared" :key="k" size="small" label class="font-mono">{{ k }}</v-chip>
          </div>
          <PermissionTip :reason="declareDenial">
            <v-btn size="small" prepend-icon="mdi-variable" :disabled="!!declareDenial" @click="emit('open', 'editor')">
              Declare in Editor
            </v-btn>
          </PermissionTip>
        </v-card-text>
      </v-card>
    </template>
  </div>
</template>

<style scoped>
.overview-view {
  min-width: 0;
}
.app-card__title {
  font-size: 1rem;
  font-weight: 700;
  line-height: 1.4;
  white-space: normal;
}
.env-card {
  border-top: 3px solid var(--env-color);
}
.metrics {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
  margin: 0;
}
.metric {
  background: rgb(var(--v-theme-surface-light));
  border-radius: 8px;
  padding: 8px 10px;
  min-width: 0;
}
.metric dt {
  font-size: 0.72rem;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
  overflow-wrap: anywhere;
}
.metric dd {
  font-size: 1.25rem;
  font-weight: 700;
  margin: 2px 0 0;
}
.metric.bad dd {
  color: rgb(var(--v-theme-warning));
}
@media (max-width: 420px) {
  .metrics {
    grid-template-columns: 1fr;
  }
}
</style>
