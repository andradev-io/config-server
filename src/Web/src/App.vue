<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useDisplay, useTheme } from 'vuetify'
import { getDisplayApiBaseUrl } from './api/config'
import { compactGrants, grantLabel } from './auth/grants'
import { useAuth } from './auth/session'
import AppLogo from './components/AppLogo.vue'
import DemoBanner from './components/DemoBanner.vue'
import ContextSelectors from './components/ContextSelectors.vue'
import { useContext } from './composables/useContext'
import { allowedView, DEFAULT_VIEW, navSections, type ViewId } from './utils/nav'
import AccessView from './views/AccessView.vue'
import ApiKeysView from './views/ApiKeysView.vue'
import CompareView from './views/CompareView.vue'
import ConfigEditor from './views/ConfigEditor.vue'
import EnvironmentsView from './views/EnvironmentsView.vue'
import MergedConfigView from './views/MergedConfigView.vue'
import OverridesView from './views/OverridesView.vue'
import OverviewView from './views/OverviewView.vue'
import VariablesView from './views/VariablesView.vue'

// ---- theme
const THEME_KEY = 'config-server-theme'
const theme = useTheme()
const isDark = computed(() => theme.global.name.value === 'dark')
function toggleTheme() {
  const next = isDark.value ? 'light' : 'dark'
  theme.change(next)
  try {
    localStorage.setItem(THEME_KEY, next)
  } catch {
    /* storage unavailable */
  }
}

// ---- auth: the views mount after the first successful sign-in and stay mounted (hidden) behind the
// login screen if the session expires, so pending edits survive until the user signs in again.
const { state: auth, permissions, grants, login, logout, retry } = useAuth()
const grantChips = computed(() =>
  compactGrants(grants.value).map((g) => ({ label: grantLabel(g), color: g.permission === 'read' ? undefined : 'neutral' })),
)
const ready = ref(false)
const signedIn = computed(() => auth.status === 'authenticated')
watch(
  () => auth.status,
  (s) => {
    if (s === 'authenticated') ready.value = true
  },
  { immediate: true },
)
const signingIn = ref(false)
async function signIn() {
  signingIn.value = true
  try {
    await login()
  } finally {
    signingIn.value = false
  }
}

// ---- global context (application + environment), loaded once signed in
const ctx = useContext()
watch(
  signedIn,
  (s) => {
    if (s && !ctx.state.loaded && !ctx.state.loading) void ctx.refresh()
  },
  { immediate: true },
)
// Different grants (another user signed in after the session expired): re-check the context.
watch(
  () => JSON.stringify(grants.value),
  () => {
    if (signedIn.value && ctx.state.loaded) void ctx.refresh()
  },
)
const pendingAppDialog = computed({
  get: () => ctx.state.pendingApp !== undefined,
  set: (v: boolean) => {
    if (!v) ctx.cancelPendingApp()
  },
})
const pendingSources = computed(() => (pendingAppDialog.value ? ctx.unsavedLabels() : []))

// ---- navigation drawer: rail on desktop (remembered), temporary on narrow screens
const { smAndDown } = useDisplay()
const RAIL_KEY = 'config-server-nav-rail'
const rail = ref(
  (() => {
    try {
      return localStorage.getItem(RAIL_KEY) === '1'
    } catch {
      return false
    }
  })(),
)
const drawer = ref(!smAndDown.value)
watch(smAndDown, (narrow) => (drawer.value = !narrow))
const railMode = computed(() => !smAndDown.value && rail.value)
function toggleNav() {
  if (smAndDown.value) {
    drawer.value = !drawer.value
    return
  }
  rail.value = !rail.value
  try {
    localStorage.setItem(RAIL_KEY, rail.value ? '1' : '0')
  } catch {
    /* storage unavailable */
  }
}
const sections = computed(() => navSections(permissions.value))

// ---- current screen (no router). Screens mount on first visit and stay alive (state is preserved).
const mode = ref<ViewId>(DEFAULT_VIEW)
const visited = reactive(new Set<ViewId>())
watch(mode, (m) => visited.add(m), { immediate: true })
// The API Keys and Access screens exist only for config:admin.
watch(permissions, (p) => (mode.value = allowedView(mode.value, p)))
const isActive = (v: ViewId) => signedIn.value && mode.value === v

// Leaving Overrides with unsaved changes asks first (the changes are kept if the user leaves anyway).
const overridesDirty = ref(false)
const leaveGuard = ref<{ show: boolean; to: ViewId }>({ show: false, to: DEFAULT_VIEW })
function navigate(target: ViewId) {
  const to = allowedView(target, permissions.value)
  if (smAndDown.value) drawer.value = false
  if (to === mode.value) return
  if (mode.value === 'overrides' && overridesDirty.value) leaveGuard.value = { show: true, to }
  else mode.value = to
}
function confirmLeave() {
  leaveGuard.value.show = false
  mode.value = leaveGuard.value.to
}

// Quick actions from the Overview cards (optionally for one environment).
const editorIntent = ref<{ env: string; n: number } | null>(null)
function openFrom(view: ViewId, env?: string) {
  if (env) ctx.setEnv(env)
  if (view === 'editor' && env) editorIntent.value = { env, n: (editorIntent.value?.n ?? 0) + 1 }
  navigate(view)
}

const apiBase = getDisplayApiBaseUrl()
</script>

<template>
  <v-app>
    <DemoBanner :demo="auth.demo" />
    <v-app-bar color="surface" density="comfortable" :extension-height="60" class="app-bar">
      <template #prepend>
        <v-app-bar-nav-icon
          v-if="signedIn"
          :aria-label="smAndDown ? 'Open navigation' : rail ? 'Expand navigation' : 'Collapse navigation'"
          @click="toggleNav"
        />
      </template>
      <div class="brand" :class="{ 'ml-3': !signedIn }">
        <AppLogo :size="26" />
        <span class="brand-name">Config Server</span>
      </div>
      <v-spacer />
      <ContextSelectors v-if="signedIn && !smAndDown" class="mr-2" />
      <v-btn
        :icon="isDark ? 'mdi-weather-sunny' : 'mdi-weather-night'"
        :aria-label="isDark ? 'Switch to light theme' : 'Switch to dark theme'"
        @click="toggleTheme"
      />
      <v-menu v-if="auth.mode === 'oidc' && signedIn && auth.me" location="bottom end">
        <template #activator="{ props: menuProps }">
          <v-btn v-bind="menuProps" icon="mdi-account-circle-outline" aria-label="Account" />
        </template>
        <v-card min-width="240" max-width="320">
          <v-card-text>
            <div class="text-subtitle-2 text-break">{{ auth.me.name || 'Signed in' }}</div>
            <div class="text-caption text-medium-emphasis mt-2 mb-1">Access</div>
            <div class="d-flex flex-wrap ga-1" aria-label="Access grants">
              <v-chip v-for="g in grantChips" :key="g.label" size="small" label :color="g.color" class="font-mono">
                {{ g.label }}
              </v-chip>
              <span v-if="!grantChips.length" class="text-medium-emphasis">no applications assigned</span>
            </div>
            <div class="text-caption text-medium-emphasis mt-2 mb-1">Roles</div>
            <div class="d-flex flex-wrap ga-1">
              <v-chip v-for="r in auth.me.roles" :key="r" size="small" label class="font-mono">{{ r }}</v-chip>
              <span v-if="!auth.me.roles.length" class="text-medium-emphasis">none</span>
            </div>
          </v-card-text>
          <v-card-actions>
            <v-spacer />
            <v-btn prepend-icon="mdi-logout" @click="logout">Sign out</v-btn>
          </v-card-actions>
        </v-card>
      </v-menu>
      <template v-if="signedIn && smAndDown" #extension>
        <div class="px-3 pb-3 w-100">
          <ContextSelectors stacked />
        </div>
      </template>
    </v-app-bar>

    <v-navigation-drawer
      v-if="signedIn"
      v-model="drawer"
      :rail="railMode"
      :temporary="smAndDown"
      :permanent="!smAndDown"
      floating
      width="236"
      color="surface"
      class="app-drawer"
    >
      <nav aria-label="Main navigation">
        <v-list nav density="compact" color="primary">
          <template v-for="s in sections" :key="s.group?.id ?? 'top'">
            <template v-if="s.group">
              <v-divider v-if="railMode" class="my-2 mx-2" />
              <v-list-subheader v-else class="nav-group">{{ s.group.title }}</v-list-subheader>
            </template>
            <v-list-item
              v-for="item in s.items"
              :key="item.id"
              :prepend-icon="item.icon"
              :title="item.title"
              :active="mode === item.id"
              :aria-current="mode === item.id ? 'page' : undefined"
              :aria-label="railMode ? item.title : undefined"
              rounded="lg"
              @click="navigate(item.id)"
            >
              <v-tooltip v-if="railMode" activator="parent" location="end">{{ item.title }}</v-tooltip>
            </v-list-item>
          </template>
        </v-list>
      </nav>
    </v-navigation-drawer>

    <v-main class="app-main">
      <v-container v-if="auth.status === 'loading' && !ready" class="py-8 text-center">
        <v-progress-circular indeterminate class="mb-3" />
        <div class="text-medium-emphasis">Connecting to the config server...</div>
      </v-container>
      <v-container v-else-if="auth.status === 'error'" class="py-4">
        <v-alert type="error" variant="tonal" title="Cannot start">
          <div class="d-flex align-center ga-2">
            <span>{{ auth.error }}</span>
            <v-spacer />
            <v-btn size="small" @click="retry">Retry</v-btn>
          </div>
        </v-alert>
        <div class="text-caption text-medium-emphasis mt-2">API: {{ apiBase }}</div>
      </v-container>
      <v-container v-else-if="auth.status === 'unauthenticated'" class="py-8">
        <v-card max-width="420" class="mx-auto">
          <v-card-title>Sign in</v-card-title>
          <v-card-text>
            <p class="mb-3">This config server requires you to sign in.</p>
            <v-alert v-if="auth.notice" type="warning" variant="tonal" density="compact">{{ auth.notice }}</v-alert>
          </v-card-text>
          <v-card-actions>
            <v-spacer />
            <v-btn color="primary" variant="flat" prepend-icon="mdi-login" :loading="signingIn" @click="signIn">
              Sign in
            </v-btn>
          </v-card-actions>
        </v-card>
      </v-container>

      <div v-if="ready" v-show="signedIn">
        <div v-if="visited.has('overview')" v-show="mode === 'overview'" class="page">
          <OverviewView :active="isActive('overview')" @open="openFrom" />
        </div>
        <div v-if="visited.has('view')" v-show="mode === 'view'" class="page">
          <MergedConfigView :active="isActive('view')" :dark="isDark" />
        </div>
        <div v-if="visited.has('compare')" v-show="mode === 'compare'" class="page">
          <CompareView :active="isActive('compare')" />
        </div>
        <div v-if="visited.has('editor')" v-show="mode === 'editor'" class="page page--wide">
          <ConfigEditor :dark="isDark" :open-env="editorIntent" />
        </div>
        <div v-if="visited.has('environments')" v-show="mode === 'environments'" class="page">
          <EnvironmentsView />
        </div>
        <div v-if="visited.has('variables')" v-show="mode === 'variables'" class="page">
          <VariablesView />
        </div>
        <div v-if="visited.has('overrides')" v-show="mode === 'overrides'" class="page page--wide">
          <OverridesView :dark="isDark" @dirty="overridesDirty = $event" @open-editor="navigate('editor')" />
        </div>
        <div v-if="visited.has('keys') && permissions.admin" v-show="mode === 'keys'" class="page">
          <ApiKeysView />
        </div>
        <div v-if="visited.has('access') && permissions.admin" v-show="mode === 'access'" class="page">
          <AccessView />
        </div>
      </div>
    </v-main>

    <!-- Leaving Overrides with pending changes -->
    <v-dialog v-model="leaveGuard.show" max-width="420">
      <v-card>
        <v-card-title class="text-subtitle-1">Unsaved overrides</v-card-title>
        <v-card-text>The Overrides screen has unsaved changes. They stay pending if you leave, but are not saved.</v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="leaveGuard.show = false">Stay</v-btn>
          <v-btn color="warning" variant="flat" @click="confirmLeave">Leave</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>

    <!-- Switching application with unsaved edits somewhere -->
    <v-dialog v-model="pendingAppDialog" max-width="440">
      <v-card>
        <v-card-title class="text-subtitle-1">Switch application?</v-card-title>
        <v-card-text>
          Unsaved changes in <strong>{{ pendingSources.join(', ') || 'this screen' }}</strong> will be discarded if you
          switch to <strong class="font-mono">{{ ctx.state.pendingApp ?? 'no application' }}</strong>.
        </v-card-text>
        <v-card-actions>
          <v-spacer />
          <v-btn @click="ctx.cancelPendingApp()">Keep editing</v-btn>
          <v-btn color="warning" variant="flat" @click="ctx.confirmPendingApp()">Discard and switch</v-btn>
        </v-card-actions>
      </v-card>
    </v-dialog>
  </v-app>
</template>

<style>
.app-bar .v-toolbar__content {
  gap: 4px;
}
.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  padding-right: 8px;
}
.brand-name {
  font-weight: 700;
  font-size: 1rem;
  letter-spacing: 0.01em;
  white-space: nowrap;
}
.nav-group {
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.08em;
}
.app-main {
  min-width: 0;
}
.page {
  padding: 24px;
  max-width: 1440px;
  margin: 0 auto;
  min-width: 0;
}
.page--wide {
  max-width: none;
}
@media (max-width: 599.98px) {
  .page {
    padding: 16px;
  }
  .brand-name {
    font-size: 0.9rem;
  }
}
</style>
