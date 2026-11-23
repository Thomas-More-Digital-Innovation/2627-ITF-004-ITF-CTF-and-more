<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import * as api from './api'

const envs = ref<any>(null)
const name = ref('')
const type = ref<api.EnvironmentType>('kali-lab')
const output = ref('')
const busy = ref(false)

// kali-lab settings; empty fields fall back to the backend defaults (appsettings "KaliLab")
const kali = ref({ vmCount: 1, cores: null as number | null, memoryMb: null as number | null, diskGb: null as number | null })
// Any other config keys, as JSON (e.g. {"bios": "ovmf"} or CtfCluster keys for ctf)
const extraConfig = ref('')

const nameValid = computed(() => /^[a-z][a-z0-9-]{0,30}$/.test(name.value))

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

function buildConfig(): Record<string, unknown> | undefined {
  const config: Record<string, unknown> = {}
  if (type.value === 'kali-lab') {
    for (const [k, v] of Object.entries(kali.value)) if (v !== null && v !== ('' as any)) config[k] = v
  }
  if (extraConfig.value.trim()) Object.assign(config, JSON.parse(extraConfig.value))
  return Object.keys(config).length ? config : undefined
}

const get = () => run(() => api.getEnvironment(name.value))
const outputs = () => run(() => api.getEnvironmentOutputs(name.value))
const create = () => run(() => api.createEnvironment(name.value, type.value, buildConfig())).then(refresh)
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
  <p>
    <input v-model="name" placeholder="environment name" />
    <small v-if="name && !nameValid"> lowercase letters, digits and "-", starting with a letter (max 31)</small>
  </p>
  <p>
    <label>Type
      <select v-model="type">
        <option value="kali-lab">kali-lab (Kali VMs + Guacamole)</option>
        <option value="ctf">ctf (Talos cluster + CTFd)</option>
      </select>
    </label>
  </p>
  <p v-if="type === 'kali-lab'">
    <label>VMs <input v-model.number="kali.vmCount" type="number" min="1" max="250" /></label>
    <label>Cores <input v-model.number="kali.cores" type="number" min="1" placeholder="default" /></label>
    <label>Memory (MiB) <input v-model.number="kali.memoryMb" type="number" min="1024" step="1024" placeholder="default" /></label>
    <label>Disk (GiB) <input v-model.number="kali.diskGb" type="number" min="20" placeholder="default" /></label>
  </p>
  <p>
    <textarea v-model="extraConfig" rows="3" cols="60" placeholder='extra config JSON, e.g. {"bios": "ovmf"}'></textarea>
  </p>
  <button :disabled="busy || !name" @click="get">Get</button>
  <button :disabled="busy || !nameValid" @click="create">Create / Apply</button>
  <button :disabled="busy || !name" @click="outputs">Outputs</button>
  <button :disabled="busy || !name" @click="destroy">Destroy</button>

  <h2>Output</h2>
  <pre>{{ output }}</pre>
</template>
