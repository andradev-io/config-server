<script setup lang="ts">
// Environment chip in the environment's stable colour.
import { envColor } from '../utils/envColor'
import EnvDot from './EnvDot.vue'

withDefaults(defineProps<{ name: string; size?: 'x-small' | 'small' | 'default'; link?: boolean }>(), {
  size: 'small',
  link: false,
})
defineEmits<{ click: [MouseEvent] }>()
</script>

<template>
  <v-chip :color="envColor(name)" :size="size" :link="link" label class="env-chip font-mono" @click="$emit('click', $event)">
    <EnvDot :name="name" :size="size === 'x-small' ? 6 : 8" class="mr-2" />
    {{ name }}
    <slot />
  </v-chip>
</template>
