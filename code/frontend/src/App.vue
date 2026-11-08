<script setup lang="ts">
import { ref, onMounted } from 'vue'
import * as api from './api'

const envs = ref<any>(null)
const name = ref('')
const output = ref('')
const busy = ref(false)

async function run(fn: () => Promise<any>) {
  busy.value = true
  output.value = '...'
  try {
    output.value = JSON.stringify(await fn(), null, 2)
  } catch (e: any) {
    output.value = `Error ${e.response?.status ?? ''}: ${JSON.stringify(e.response?.data ?? e.message, null, 2)}`
  } finally {
    busy.value = false
  }
}

async function refresh() {
  try { envs.value = await api.getEnvironments() } catch (e: any) { envs.value = e.message }
}

const get = () => run(() => api.getEnvironment(name.value))
const create = () => run(() => api.createEnvironment(name.value)).then(refresh)
const destroy = () => {
  if (confirm(`Destroy "${name.value}"?`)) run(() => api.deleteEnvironment(name.value)).then(refresh)
}

onMounted(refresh)
</script>

<template>
  <h1>CTF Environments</h1>

  <h2>All environments <button @click="refresh">Refresh</button></h2>
  <pre>{{ JSON.stringify(envs, null, 2) }}</pre>

  <h2>Environment</h2>
  <input v-model="name" placeholder="environment name" />
  <button :disabled="busy || !name" @click="get">Get</button>
  <button :disabled="busy || !name" @click="create">Create / Apply</button>
  <button :disabled="busy || !name" @click="destroy">Destroy</button>

  <h2>Output</h2>
  <pre>{{ output }}</pre>
</template>
