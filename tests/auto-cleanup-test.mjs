// 自动清理与用完即净生命周期测试（mock ctx + SaHelper）
import assert from 'node:assert/strict'

const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')

const tools = new Map()
const ctx = {
  tools: {
    register(def) {
      tools.set(def.name, def)
      return def
    },
  },
  on() {},
  effect(fn) {
    fn()
  },
  injectToSession() {},
  agents: {
    currentInitiator: () => undefined,
    get: () => undefined,
  },
  log() {},
  warn() {},
}

mod.apply(ctx)
await new Promise((r) => setTimeout(r, 800))

console.log('=== Test 1: saperm_overlay_open + saperm_infer_start -> saperm_infer_stop 自动关闭 overlay ===')
const openRes = await tools.get('saperm_overlay_open').execute({})
console.log('overlay_open:', JSON.stringify(openRes))

const start1 = await tools.get('saperm_infer_start').execute({
  objects: false,
  scene: false,
  maxBoxes: 10,
})
console.log('infer_start (Test 1):', JSON.stringify({ running: start1.running }))

await new Promise((r) => setTimeout(r, 500))

const stop1 = await tools.get('saperm_infer_stop').execute({})
console.log('infer_stop (Test 1):', JSON.stringify(stop1))

const health1 = await tools.get('saperm_health').execute({})
console.log('health after stop (Test 1):', JSON.stringify({
  overlayOpen: health1.overlayOpen,
  observation: health1.observation,
}))

assert.equal(health1.overlayOpen, false, 'Test 1 failed: expected health.overlayOpen === false')
assert.equal(health1.observation.mode, null, 'Test 1 failed: expected observation.mode === null')
assert.equal(health1.observation.holds, 0, 'Test 1 failed: expected observation.holds === 0')
console.log('[PASS] Test 1 passed')

console.log('=== Test 2: saperm_infer_start({ ttlSec: 3 }) -> 4200ms 无操作自动回收 ===')
const start2 = await tools.get('saperm_infer_start').execute({
  ttlSec: 3,
  objects: false,
  scene: false,
  maxBoxes: 10,
})
console.log('infer_start (Test 2):', JSON.stringify({ running: start2.running, ttlSec: start2.ttlSec }))

// 等待 4200ms，期间不调用任何工具
await new Promise((r) => setTimeout(r, 4200))

const health2 = await tools.get('saperm_health').execute({})
console.log('health after TTL (Test 2):', JSON.stringify({
  overlayOpen: health2.overlayOpen,
  observation: health2.observation,
}))

assert.equal(health2.observation.mode, null, 'Test 2 failed: expected observation.mode === null')
assert.equal(health2.observation.holds, 0, 'Test 2 failed: expected observation.holds === 0')
assert.equal(health2.overlayOpen, false, 'Test 2 failed: expected overlayOpen === false')

const hist = await tools.get('saperm_history').execute({ limit: 30 })
const autoCleanupEntry = Array.isArray(hist.items)
  ? hist.items.find((item) => item && item.type === 'auto_cleanup')
  : null
console.log('latest auto_cleanup history entry:', JSON.stringify(autoCleanupEntry))
assert.ok(autoCleanupEntry, 'Test 2 failed: expected saperm_history to contain an entry with type === "auto_cleanup"')
console.log('[PASS] Test 2 passed')

console.log('ALL AUTO-CLEANUP TESTS PASSED')
process.exit(0)
