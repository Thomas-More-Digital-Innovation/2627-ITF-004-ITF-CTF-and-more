import axios from 'axios'

const apiClient = axios.create({
  baseURL: '/api',
  headers: {
    'Content-type': 'application/json'
  }
})

export const getHello = async () => {
  const response = await apiClient.get('/hello')
  return response.data
}