import { createApp } from 'vue'
import App from './App.vue'
import { completeSilentCallback, initAuth } from './auth/session'
import { isAuthCallback } from './auth/oidcConfig'
import { createAppVuetify } from './theme/vuetify'

const THEME_KEY = 'config-server-theme'

function initialTheme(): 'light' | 'dark' {
  try {
    const saved = localStorage.getItem(THEME_KEY)
    if (saved === 'light' || saved === 'dark') return saved
  } catch {
    /* storage unavailable */
  }
  return 'dark'
}

const inFrame = (() => {
  try {
    return window.self !== window.top
  } catch {
    return true
  }
})()

async function start() {
  // Silent token renew loads the SPA in a hidden iframe: only forward the response, never mount the app.
  if (inFrame && isAuthCallback(window.location.search)) {
    await completeSilentCallback().catch(() => undefined)
    return
  }
  createApp(App).use(createAppVuetify(initialTheme())).mount('#app')
  await initAuth()
}

void start()
