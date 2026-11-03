<script setup lang="ts">
// Small colour dot for an environment (stable colour per name, follows the theme).
import { computed } from 'vue'
import { envColorCss } from '../utils/envColor'

const props = withDefaults(defineProps<{ name: string; size?: number }>(), { size: 8 })
const style = computed(() => ({ background: envColorCss(props.name), width: `${props.size}px`, height: `${props.size}px` }))
</script>

<template>
  <span class="env-dot" :style="style" aria-hidden="true" />
</template>

<style scoped>
.env-dot {
  display: inline-block;
  flex: none;
  border-radius: 50%;
  vertical-align: middle;
}
</style>
