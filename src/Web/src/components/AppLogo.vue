<script setup lang="ts">
// "Split" mark from the portfolio site: a V in the text colour interlocked with its mirror (the A) in the accent.
withDefaults(defineProps<{ size?: number }>(), { size: 32 })
</script>

<template>
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 19.74 135.47 96"
    :style="{ height: `${size}px` }"
    class="app-logo"
    stroke-linecap="round"
    stroke-linejoin="round"
    role="img"
    aria-label="Logo"
    focusable="false"
  >
    <path d="M45.4 103.5 V84.5 L67.74 32 L90.1 84.5 V103.5" fill="none" stroke="#b4b96b" stroke-opacity="0.9" stroke-width="10" />
    <path d="M45.4 32 V51 L67.74 103.5 L90.1 51 V32" fill="none" stroke="currentColor" stroke-opacity="0.85" stroke-width="10" />
  </svg>
</template>

<style scoped>
.app-logo {
  display: inline-block;
  width: auto;
  flex: none;
  overflow: visible;
  vertical-align: middle;
}
</style>
