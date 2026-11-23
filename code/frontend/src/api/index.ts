import axios from 'axios'

const apiClient = axios.create({
  baseURL: '/api',
  headers: { 'Content-type': 'application/json' },
})

export const getEnvironments = async () => (await apiClient.get('/environments')).data
export const getEnvironment = async (name: string) =>
  (await apiClient.get(`/environments/${encodeURIComponent(name)}`)).data
export type EnvironmentType = 'ctf' | 'kali-lab'

export const createEnvironment = async (name: string, type: EnvironmentType, config?: Record<string, unknown>) =>
  (await apiClient.post('/environments/create', { name, type, config })).data
export const getEnvironmentOutputs = async (name: string) =>
  (await apiClient.get(`/environments/${encodeURIComponent(name)}/outputs`)).data
export const deleteEnvironment = async (name: string) =>
  (await apiClient.delete(`/environments/${encodeURIComponent(name)}`)).data
