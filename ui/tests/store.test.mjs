import test from 'node:test'
import assert from 'node:assert/strict'
import { setImmediate as nextTurn } from 'node:timers/promises'
import { createStore, deferred, settingsFixture } from './helpers.mjs'

test('rapid settings edits coalesce and preserve unrelated nested values', async () => {
  const writes = []
  const { store } = await createStore({ saveSettings: async (value) => { writes.push(structuredClone(value)); return value } })
  const first = store.getState().patchSettings({ appearance: { accent: '#5ecf8e' } })
  const second = store.getState().patchSettings({ appearance: { radius: 12 }, dns: { ipv6: true } })
  await Promise.all([first, second])
  assert.equal(writes.length, 1, 'edits in the same turn should issue one PUT')
  assert.equal(writes[0].appearance.accent, '#5ecf8e')
  assert.equal(writes[0].appearance.radius, 12)
  assert.equal(writes[0].appearance.trafficGraph, true)
  assert.deepEqual(writes[0].dns.nameserver, ['https://1.1.1.1/dns-query'])
  assert.equal(store.getState().settings.dns.ipv6, true)
  assert.equal(store.getState().patching, false)
})

test('edits during a slow save are serialized and the latest edit reaches the host', async () => {
  const firstWrite = deferred()
  const entered = deferred()
  const writes = []
  let active = 0, maxActive = 0
  const { store } = await createStore({ saveSettings: async (value) => {
    writes.push(structuredClone(value))
    active++; maxActive = Math.max(maxActive, active)
    if (writes.length === 1) { entered.resolve(); await firstWrite.promise }
    active--
    return structuredClone(value)
  } })
  const first = store.getState().patchSettings({ mode: 'global' })
  await entered.promise
  const second = store.getState().patchSettings({ appearance: { accent: '#abcdef' } })
  const third = store.getState().patchSettings({ mode: 'direct' })
  assert.equal(writes.length, 1, 'a newer PUT must wait for the older one')
  firstWrite.resolve()
  await Promise.all([first, second, third])
  assert.equal(maxActive, 1)
  assert.equal(writes.length, 2)
  assert.equal(writes[1].mode, 'direct')
  assert.equal(writes[1].appearance.accent, '#abcdef')
  assert.equal(store.getState().settings.mode, 'direct')
  assert.equal(store.getState().settings.appearance.accent, '#abcdef')
})

test('refresh responses begun before an edit cannot overwrite the saved edit', async () => {
  const staleRead = deferred()
  const { store } = await createStore({ settings: () => staleRead.promise })
  const refreshing = store.getState().refresh()
  await store.getState().patchSettings({ mode: 'global' })
  staleRead.resolve(settingsFixture())
  await refreshing
  assert.equal(store.getState().settings.mode, 'global')
  assert.equal(store.getState().status.coreInstalled, true, 'status is still refreshed')
  assert.equal(store.getState().profiles.length, 1)
})

test('a refresh arriving while a write is pending preserves optimistic settings', async () => {
  const save = deferred(), entered = deferred()
  const { store } = await createStore({ saveSettings: async (value) => { entered.resolve(); await save.promise; return value } })
  const patch = store.getState().patchSettings({ dns: { ipv6: true } })
  await entered.promise
  await store.getState().refresh()
  assert.equal(store.getState().settings.dns.ipv6, true)
  assert.equal(store.getState().patching, true)
  save.resolve()
  await patch
  assert.equal(store.getState().settings.dns.ipv6, true)
})

test('overlapping refreshes share a request and unchanged data retains references', async () => {
  const read = deferred()
  let reads = 0
  const { store } = await createStore({ settings: () => { reads++; return read.promise } })
  const first = store.getState().refresh(), second = store.getState().refresh()
  assert.equal(first, second)
  assert.equal(reads, 1)
  read.resolve(settingsFixture())
  await Promise.all([first, second])
  const previous = store.getState()
  await store.getState().refresh()
  const next = store.getState()
  assert.equal(reads, 2, 'the dedupe lock must release after completion')
  assert.equal(next.settings, previous.settings)
  assert.equal(next.status, previous.status)
  assert.equal(next.profiles, previous.profiles)
})

test('failed saves roll back and release patching so a later save can succeed', async () => {
  let fail = true
  const { store, initialSettings } = await createStore({ saveSettings: async (value) => {
    if (fail) throw new Error('disk unavailable')
    return value
  } })
  await assert.rejects(store.getState().patchSettings({ mode: 'global' }), /disk unavailable/)
  assert.deepEqual(store.getState().settings, initialSettings)
  assert.equal(store.getState().error, 'disk unavailable')
  assert.equal(store.getState().patching, false)
  fail = false
  await store.getState().patchSettings({ mode: 'direct' })
  assert.equal(store.getState().settings.mode, 'direct')
  assert.equal(store.getState().patching, false)
  assert.equal(store.getState().error, '', 'a successful retry must dismiss the previous save error')
})

test('failure of a later queued write restores the last confirmed settings', async () => {
  const firstWrite = deferred(), entered = deferred()
  let writes = 0
  const { store } = await createStore({ saveSettings: async (value) => {
    if (++writes === 1) { entered.resolve(); await firstWrite.promise; return value }
    throw new Error('second write failed')
  } })
  const first = store.getState().patchSettings({ mode: 'global' })
  await entered.promise
  const second = store.getState().patchSettings({ mode: 'direct' })
  firstWrite.resolve()
  const results = await Promise.allSettled([first, second])
  for (const result of results) {
    assert.equal(result.status, 'rejected', 'every caller awaiting the save batch must receive the failure')
    assert.match(result.reason.message, /second write failed/)
  }
  assert.equal(store.getState().settings.mode, 'global')
  assert.equal(store.getState().error, 'second write failed')
  assert.equal(store.getState().patching, false)
})

test('busy operations cannot overlap and the guard releases on failure', async () => {
  const gate = deferred()
  const { store } = await createStore()
  let calls = 0
  const first = store.getState().run(async () => { calls++; await gate.promise; throw new Error('core failed') })
  assert.equal(store.getState().busy, true)
  await store.getState().run(async () => { calls++ })
  assert.equal(calls, 1)
  gate.resolve()
  await first
  assert.equal(store.getState().busy, false)
  assert.equal(store.getState().error, 'core failed')
  await store.getState().run(async () => { calls++ })
  assert.equal(calls, 2)
  assert.equal(store.getState().busy, false)
})

test('a network operation waits for pending settings and stops if that save fails', async () => {
  const save = deferred(), entered = deferred()
  const { store } = await createStore({ saveSettings: async () => { entered.resolve(); return save.promise } })
  const patch = store.getState().patchSettings({ mode: 'global' })
  await entered.promise
  let started = false
  const action = store.getState().run(async () => { started = true })
  assert.equal(started, false)
  assert.equal(store.getState().busy, true)
  save.reject(new Error('settings could not be saved'))
  await Promise.all([assert.rejects(patch, /settings could not be saved/), action])
  assert.equal(started, false)
  assert.equal(store.getState().busy, false)
  assert.equal(store.getState().patching, false)
  assert.equal(store.getState().error, 'settings could not be saved')
})

test('a failed inline settings save prevents the command from restarting the core', async () => {
  let restarts = 0
  const { store, api, initialSettings } = await createStore({
    saveSettings: async () => { throw new Error('configuration write failed') },
    restart: async () => { restarts++ },
  })
  await store.getState().run(async () => {
    await store.getState().patchSettings({ mode: 'global' })
    await api.restart()
  })
  assert.equal(restarts, 0, 'network actions must not continue with unsaved settings')
  assert.deepEqual(store.getState().settings, initialSettings)
  assert.equal(store.getState().error, 'configuration write failed')
  assert.equal(store.getState().patching, false)
  assert.equal(store.getState().busy, false)
})

test('a field handler may ignore a rejected save without an unhandled rejection', async () => {
  const { store, initialSettings } = await createStore({
    saveSettings: async () => { throw new Error('field save failed') },
  })
  // node:test reports an unhandledRejection as a failure; deliberately leave
  // this promise unobserved to exercise the fire-and-forget field handler path.
  store.getState().patchSettings({ mode: 'global' })
  await nextTurn()
  assert.deepEqual(store.getState().settings, initialSettings)
  assert.equal(store.getState().error, 'field save failed')
  assert.equal(store.getState().patching, false)
})

test('duplicate live samples do not notify subscribers or replace the live object', async () => {
  const { store } = await createStore()
  const initialLive = store.getState().live
  let updates = 0
  const unsubscribe = store.subscribe(() => updates++)
  store.getState().setLive({ up: 0, down: 0 })
  assert.equal(updates, 0)
  assert.equal(store.getState().live, initialLive)
  store.getState().setLive({ up: 1024, mem: 2048 })
  const changed = store.getState().live
  store.getState().setLive({ up: 1024, mem: 2048 })
  assert.equal(updates, 1)
  assert.equal(store.getState().live, changed)
  store.getState().setLive({ now: 'Test node' })
  assert.equal(updates, 2)
  assert.equal(store.getState().live.up, 1024)
  unsubscribe()
})
