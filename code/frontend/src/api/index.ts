import axios from 'axios'

const apiClient = axios.create({
  baseURL: '/api',
  headers: { 'Content-type': 'application/json' },
})

export const getEnvironments = async () => (await apiClient.get('/environments')).data
export const getEnvironment = async (name: string) =>
  (await apiClient.get(`/environments/${encodeURIComponent(name)}`)).data
export const createEnvironment = async (name: string) =>
  (await apiClient.post('/environments/create', JSON.stringify(name))).data
export const deleteEnvironment = async (name: string) =>
  (await apiClient.delete(`/environments/${encodeURIComponent(name)}`)).data
