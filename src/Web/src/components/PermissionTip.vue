<script setup lang="ts">
// Explains why a control is disabled by the caller's grants. Disabled buttons get no pointer or focus
// events, so the tooltip hangs on a focusable wrapper. Without a reason the slot renders as is.
withDefaults(defineProps<{ reason?: string | null; location?: 'top' | 'bottom' | 'start' | 'end' }>(), {
  reason: null,
  location: 'top',
})
</script>

<template>
  <v-tooltip v-if="reason" :text="reason" :location="location" max-width="320">
    <template #activator="{ props: tip }">
      <span v-bind="tip" class="permission-tip" tabindex="0" role="group" :aria-label="reason">
        <slot />
      </span>
    </template>
  </v-tooltip>
  <slot v-else />
</template>

<style scoped>
.permission-tip {
  display: inline-flex;
  max-width: 100%;
  border-radius: 8px;
}
.permission-tip:focus-visible {
  outline: 2px solid rgb(var(--v-theme-secondary));
  outline-offset: 1px;
}
</style>
