<script setup lang="ts">
// Consistent page header: title + subtitle on the left, actions on the right (wrapping under on phones).
defineProps<{ title: string; subtitle?: string; icon?: string }>()
</script>

<template>
  <header class="page-header">
    <div class="page-header__text">
      <h1 class="page-header__title">
        <v-icon v-if="icon" :icon="icon" size="22" class="page-header__icon" aria-hidden="true" />
        <slot name="title">{{ title }}</slot>
      </h1>
      <div v-if="subtitle || $slots.subtitle" class="page-header__subtitle">
        <slot name="subtitle">{{ subtitle }}</slot>
      </div>
    </div>
    <div v-if="$slots.actions" class="page-header__actions">
      <slot name="actions" />
    </div>
  </header>
</template>

<style scoped>
.page-header {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 12px 16px;
  margin-bottom: 20px;
}
.page-header__text {
  flex: 1 1 260px;
  min-width: 0;
}
.page-header__title {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 0;
  font-size: 1.375rem;
  font-weight: 700;
  line-height: 1.3;
  overflow-wrap: anywhere;
}
.page-header__icon {
  opacity: 0.6;
}
.page-header__subtitle {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  font-size: 0.875rem;
  color: rgba(var(--v-theme-on-surface), var(--v-medium-emphasis-opacity));
  overflow-wrap: anywhere;
}
.page-header__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
</style>
