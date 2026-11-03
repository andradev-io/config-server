<script setup lang="ts">
// Global application + environment selectors (app bar). The app switch goes through the
// unsaved-changes check of the context store; the environment switch never loses edits.
import { computed, ref, watch } from 'vue'
import { useContext } from '../composables/useContext'
import EnvDot from './EnvDot.vue'

withDefaults(defineProps<{ stacked?: boolean }>(), { stacked: false })
const ctx = useContext()

const appModel = computed({
  get: () => ctx.state.app,
  set: (v: string | null) => void ctx.requestApp(v ?? null),
})
const envModel = computed({
  get: () => ctx.state.env,
  set: (v: string | null) => ctx.setEnv(v ?? null),
})
// Only the environments the caller may read in the selected application.
const envItems = computed(() => ctx.environmentsFor(ctx.state.app).map((e) => ({ title: e.name, value: e.name })))

// A refused switch (user kept editing) re-renders the autocomplete so it shows the current app again.
const appKey = ref(0)
watch(
  () => ctx.state.pendingApp,
  (now, before) => {
    if (now === undefined && before !== undefined) appKey.value++
  },
)
</script>

<template>
  <div class="context-selectors" :class="{ stacked }">
    <v-autocomplete
      :key="appKey"
      v-model="appModel"
      :items="ctx.state.apps"
      label="Application"
      aria-label="Application"
      prepend-inner-icon="mdi-cube-outline"
      density="compact"
      hide-details
      clearable
      :loading="ctx.state.loading"
      no-data-text="No applications"
      class="ctx-app"
    />
    <v-select
      v-model="envModel"
      :items="envItems"
      label="Environment"
      aria-label="Environment"
      density="compact"
      hide-details
      no-data-text="No environments"
      class="ctx-env"
    >
      <template #selection="{ item }">
        <span class="d-inline-flex align-center ga-2 font-mono text-truncate">
          <EnvDot :name="String(item.value)" />{{ item.title }}
        </span>
      </template>
      <template #item="{ props: itemProps, item }">
        <v-list-item v-bind="itemProps">
          <template #prepend><EnvDot :name="String(item.value)" class="mr-3" /></template>
        </v-list-item>
      </template>
    </v-select>
  </div>
</template>

<style scoped>
.context-selectors {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.ctx-app {
  width: 270px;
  flex: 0 1 270px;
}
.ctx-env {
  width: 170px;
  flex: 0 1 170px;
}
.stacked {
  width: 100%;
}
.stacked .ctx-app {
  flex: 1 1 60%;
  width: auto;
  min-width: 0;
}
.stacked .ctx-env {
  flex: 1 1 40%;
  width: auto;
  min-width: 0;
}
</style>
