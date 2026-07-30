import '@testing-library/jest-dom/vitest'
import { afterAll, afterEach, beforeAll } from 'vitest'
import { cleanup } from '@testing-library/react'
import { mswServer } from './msw'

// jsdom não tem ResizeObserver; o ResponsiveContainer do Recharts precisa dele.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver

// O Node 25 injeta um localStorage experimental quebrado (sem clear/setItem
// funcionais) que sombreia o do jsdom. Substituímos por um storage em memória
// para o usePersistedState funcionar nos testes.
class MemoryStorage {
  private map = new Map<string, string>()
  getItem(key: string) {
    return this.map.get(key) ?? null
  }
  setItem(key: string, value: string) {
    this.map.set(key, String(value))
  }
  removeItem(key: string) {
    this.map.delete(key)
  }
  clear() {
    this.map.clear()
  }
  key(index: number) {
    return [...this.map.keys()][index] ?? null
  }
  get length() {
    return this.map.size
  }
}
Object.defineProperty(globalThis, 'localStorage', {
  value: new MemoryStorage(),
  configurable: true,
  writable: true,
})

beforeAll(() => mswServer.listen({ onUnhandledRequest: 'error' }))
afterEach(() => {
  mswServer.resetHandlers()
  cleanup()
  // Preferências persistidas (visibilidade de métricas, layout) não podem vazar entre testes.
  globalThis.localStorage.clear()
})
afterAll(() => mswServer.close())
