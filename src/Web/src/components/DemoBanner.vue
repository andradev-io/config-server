<script setup lang="ts">
import { computed, ref } from 'vue'
import type { DemoInfo } from '../auth/api'
import { demoBannerText, dismissDemoBanner, isDemoBannerDismissed } from '../utils/demo'

const props = defineProps<{ demo: DemoInfo | null }>()

// Dismissed per browser tab session (sessionStorage), so it returns on the next visit.
const dismissed = ref(isDemoBannerDismissed())
const visible = computed(() => !!props.demo && !dismissed.value)
const text = computed(() => (props.demo ? demoBannerText(props.demo) : ''))

function dismiss() {
  dismissed.value = true
  dismissDemoBanner()
}
</script>

<template>
  <v-system-bar v-if="visible" color="surface-variant" class="demo-banner" height="36" role="status">
    <v-chip size="x-small" label color="primary" variant="flat" class="mr-2">DEMO</v-chip>
    <span class="demo-banner-text">{{ text }}</span>
    <v-spacer />
    <v-btn icon="mdi-close" size="x-small" variant="text" aria-label="Dismiss demo notice" @click="dismiss" />
  </v-system-bar>
</template>

<style scoped>
.demo-banner-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
