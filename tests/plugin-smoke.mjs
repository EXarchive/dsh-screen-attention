// 插件冒烟：mock ctx + 真实 helper
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = {
  tools: { register(def) { tools.set(def.name, def); return def } },
  on() {},
  effect(fn) { fn() },
  injectToSession() {},
  agents: { currentInitiator: () => undefined, get: () => undefined },
  log() {}, warn() {},
}
const dispose = mod.default ? mod.default(ctx) : mod.apply(ctx)
await new Promise(r => setTimeout(r, 800))
console.log('tools registered:', tools.size)
const health = await tools.get('saperm_health').execute({})
console.log('health:', JSON.stringify({ ok: health.ok, helper: health.helper, observation: health.observation, clip: health.clip, characters: health.characters }))
// 空闲保活：旧逻辑 1s 后会杀 helper，这里等 3s 后应仍在
await new Promise(r => setTimeout(r, 3000))
const { execSync } = await import('node:child_process')
let alive = 0
try { alive = Number(String(execSync('powershell -NoProfile -Command "(Get-Process SaHelper -ErrorAction SilentlyContinue | Measure-Object).Count"')).trim()) } catch {}
console.log('helper alive after 3s idle:', alive)
// 互斥：先 infer 再 interpret → interpret 应 autoStopped=infer
const inf = await tools.get('saperm_infer_start').execute({ objects: false, scene: false, maxBoxes: 20 })
console.log('infer_start:', JSON.stringify({ running: inf.running }))
await new Promise(r => setTimeout(r, 2500))
const itp = await tools.get('saperm_interpret_start').execute({ intervalMs: 800 })
console.log('interpret_start:', JSON.stringify({ running: itp.running, autoStopped: itp.autoStopped }))
await tools.get('saperm_interpret_stop').execute({})
await tools.get('saperm_infer_stop').execute({})
const h2 = await tools.get('saperm_health').execute({})
console.log('after stop observation:', JSON.stringify(h2.observation))
if (typeof dispose === 'function') { try { await dispose() } catch {} }
process.exit(0)
