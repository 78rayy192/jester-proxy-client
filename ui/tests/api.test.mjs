import test from 'node:test'
import assert from 'node:assert/strict'
import { loadSource } from './helpers.mjs'

const { api, clash, sanitizeBps, MAX_BPS, formatSpeed } = await loadSource('lib/api.ts')

test('legitimate speed increases are accepted immediately instead of freezing at the previous rate', () => {
  let speed = sanitizeBps(16 * 1024)
  speed = sanitizeBps(80 * 1024 * 1024, speed)
  assert.equal(speed, 80 * 1024 * 1024)
  assert.equal(sanitizeBps(MAX_BPS, speed), MAX_BPS)
  assert.equal(formatSpeed(speed), '80.0 MB/s')
})

test('invalid speed samples and absurd spikes cannot leak into displayed rates', () => {
  for (const value of [-1, NaN, Infinity, -Infinity, undefined, 'invalid']) assert.equal(sanitizeBps(value), 0)
  assert.equal(sanitizeBps(MAX_BPS + 1, 2048), 2048)
  assert.equal(sanitizeBps(MAX_BPS + 1, -1), 0)
  assert.equal(sanitizeBps(MAX_BPS + 1, Infinity), 0)
  assert.equal(sanitizeBps('1024'), 1024)
  assert.equal(formatSpeed(NaN), '0 B/s')
})

test('204 success responses support close, mode, and delete actions without JSON errors', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push({ url, ...init })
    return new Response(null, { status: 204 })
  })
  assert.equal(await clash.closeAll(), undefined)
  assert.equal(await clash.patchMode('global'), undefined)
  assert.equal(await api.deleteProfile('test-profile'), undefined)
  assert.deepEqual(calls.map(({ url, method }) => [url, method]), [
    ['/clash/connections', 'DELETE'], ['/clash/configs', 'PATCH'], ['/api/profiles/test-profile', 'DELETE'],
  ])
  assert.deepEqual(JSON.parse(calls[1].body), { mode: 'global' })
  assert.ok(calls.every((call) => call.signal instanceof AbortSignal), 'requests must have a timeout signal')
})

test('failed API actions surface structured server errors and messages', async (t) => {
  const responses = [
    new Response(JSON.stringify({ error: 'configuration invalid' }), { status: 400 }),
    new Response(JSON.stringify({ message: 'controller unavailable' }), { status: 503 }),
  ]
  t.mock.method(globalThis, 'fetch', async () => responses.shift())
  await assert.rejects(api.start(), /configuration invalid/)
  await assert.rejects(clash.patchMode('global'), /controller unavailable/)
})

test('non-JSON errors keep the HTTP status explanation and network errors propagate', async (t) => {
  let networkFailure = false
  t.mock.method(globalThis, 'fetch', async () => {
    if (networkFailure) throw new TypeError('connection refused')
    return new Response('upstream is unavailable', { status: 503, statusText: 'Service Unavailable' })
  })
  await assert.rejects(api.status(), /Service Unavailable/)
  networkFailure = true
  await assert.rejects(api.status(), /connection refused/)
})

test('successful JSON responses and settings request bodies retain their values', async (t) => {
  const settings = { mode: 'rule', dns: { enable: true } }
  let request
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    request = { url, ...init }
    return Response.json(settings)
  })
  assert.deepEqual(await api.saveSettings(settings), settings)
  assert.equal(request.url, '/api/settings')
  assert.equal(request.method, 'PUT')
  assert.deepEqual(JSON.parse(request.body), settings)
  assert.equal(request.headers['Content-Type'], 'application/json')
})

test('request cancellation reaches fetch and rejects the pending action', async (t) => {
  const timeout = new AbortController()
  t.mock.method(AbortSignal, 'timeout', () => timeout.signal)
  t.mock.method(globalThis, 'fetch', async (_url, init) => new Promise((_resolve, reject) => {
    init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true })
  }))
  const pending = api.status()
  timeout.abort(new DOMException('The operation timed out', 'TimeoutError'))
  await assert.rejects(pending, { name: 'TimeoutError' })
})
