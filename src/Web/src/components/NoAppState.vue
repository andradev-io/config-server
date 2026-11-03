<script setup lang="ts">
// Shown by app-scoped views while no application is selected in the global context, or (signed in
// without a single grant) the "no applications assigned" state instead of errors.
import { computed } from 'vue'
import { useAuth } from '../auth/session'
import { useContext } from '../composables/useContext'
import { hasNoAccess, NO_ACCESS_MESSAGE, NO_ACCESS_TITLE } from '../utils/access'

withDefaults(defineProps<{ message?: string }>(), { message: 'Choose an application to continue.' })
const ctx = useContext()
const { grants } = useAuth()
const noAccess = computed(() => hasNoAccess(grants.value))
</script>

<template>
  <v-card v-if="noAccess" class="no-app pa-6 text-center">
    <v-icon icon="mdi-account-lock-outline" size="36" class="mb-2 text-medium-emphasis" />
    <div class="text-subtitle-1 mb-1">{{ NO_ACCESS_TITLE }}</div>
    <div class="text-body-2 text-medium-emphasis">{{ NO_ACCESS_MESSAGE }}</div>
  </v-card>
  <v-card v-else class="no-app pa-6 text-center">
    <v-icon icon="mdi-cube-outline" size="36" class="mb-2 text-medium-emphasis" />
    <div class="text-subtitle-1 mb-1">No application selected</div>
    <div class="text-body-2 text-medium-emphasis mb-4">{{ message }}</div>
    <div v-if="ctx.state.apps.length" class="d-flex flex-wrap justify-center ga-2">
      <v-chip
        v-for="a in ctx.state.apps"
        :key="a"
        link
        label
        prepend-icon="mdi-cube-outline"
        class="font-mono"
        @click="ctx.requestApp(a)"
      >
        {{ a }}
      </v-chip>
    </div>
    <div v-else-if="ctx.state.loaded" class="text-body-2 text-medium-emphasis">No applications found.</div>
  </v-card>
</template>
