import { readFile } from 'node:fs/promises'
import ts from 'typescript'

let moduleId = 0
const apiMocks = new Map()
globalThis.__jesterRegressionApis = apiMocks

export function deferred() {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

// Exercise the shipped TypeScript source, without a second compiled test copy
// or an additional test runner dependency. Each import gets fresh module state.
export async function loadSource(relativePath, transform = (source) => source) {
  const source = await readFile(new URL(`../src/${relativePath}`, import.meta.url), 'utf8')
  const result = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext },
  })
  const id = ++moduleId
  const code = `${transform(result.outputText)}\n//# sourceURL=jester-regression-${id}.mjs\n// fresh module ${id}`
  return import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)
}

export function settingsFixture() {
  return {
    mode: 'rule', tunEnabled: true, activeProfileId: 'test-profile',
    dns: { enable: true, ipv6: false, nameserver: ['https://1.1.1.1/dns-query'], fallback: [] },
    appearance: { accent: '#e6c48a', radius: 16, trafficGraph: true },
  }
}

export async function createStore(overrides = {}) {
  const initialSettings = settingsFixture()
  const api = {
    status: async () => ({ running: false, coreInstalled: true }),
    settings: async () => structuredClone(initialSettings),
    profiles: async () => [{ id: 'test-profile', name: 'Test profile' }],
    saveSettings: async (settings) => structuredClone(settings),
    ...overrides,
  }
  const id = `api-${++moduleId}`
  apiMocks.set(id, api)
  try {
    const { useApp } = await loadSource('store/app.ts', (code) => {
      const withMock = code.replace(/import \{ api \} from ['"]\.\.\/lib\/api['"];?/, `const api = globalThis.__jesterRegressionApis.get(${JSON.stringify(id)});`)
      if (withMock === code) throw new Error('Store API import was not replaced; update the test harness')
      return withMock.replace(/from ['"]zustand['"]/, `from ${JSON.stringify(import.meta.resolve('zustand'))}`)
    })
    useApp.setState({ settings: structuredClone(initialSettings) })
    return { store: useApp, api, initialSettings }
  } finally {
    apiMocks.delete(id)
  }
}
