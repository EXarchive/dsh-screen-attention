// 讲解期间并发识图的显存争用回归：讲解不能被挤停，识图也不能超时
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = { tools: { register(d) { tools.set(d.name, d); return d } }, on() {}, effect(fn) { fn() }, injectToSession() {}, agents: { currentInitiator: () => undefined, get: () => undefined }, log() {}, warn() {} }
mod.apply(ctx)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
await sleep(1200)
const n = tools.get('saperm_narrate')
const v = tools.get('saperm_vision')
const ps = async () => { try { const j = await (await fetch('http://127.0.0.1:11434/api/ps')).json(); return (j.models || []).map(m => m.name + ':' + (m.size_vram / 1073741824).toFixed(1) + 'G') } catch { return null } }
let fails = []
await n.execute({ action: 'start', seconds: 55, periodMs: 1500 })
let last = 0
const deltas = []
for (let i = 1; i <= 6; i++) {
  await sleep(9000)
  const st = await n.execute({ action: 'status' })
  const delta = st.narrations - last; last = st.narrations
  deltas.push(delta)
  console.log('t+' + (i * 9) + 's', JSON.stringify({ n: st.narrations, d: delta, errs: st.errors, maxCycleMs: st.maxCycleMs, lastCycleMs: st.lastCycleMs, models: await ps() }))
  if (i % 2 === 0) {
    const t0 = Date.now()
    const r = await v.execute({ engine: 'vlm', full: true })
    const wall = Date.now() - t0
    console.log('   vision during narrate ->', JSON.stringify({ wallMs: wall, degraded: r.degraded, text: String(r.text || '').slice(0, 18) }))
    if (wall > 20000) fails.push('识图在讲解期间超 20s: ' + wall)
    if (r.degraded) fails.push('识图在讲解期间退化')
  }
}
const fin = await n.execute({ action: 'stop' })
console.log('stop:', JSON.stringify(fin))
// 采样窗口 9s、单条 1.5-8.3s：允许个别窗口 0 条（正在生成），但不能连续两个窗口 0 条
  const twoZero = deltas.some((d, i) => d === 0 && i > 0 && deltas[i - 1] === 0)
  if (twoZero) fails.push('连续两个采样窗口 0 条（卡顿）: ' + JSON.stringify(deltas))
console.log('RESULT:', fails.length ? 'FAIL ' + fails.join(' | ') : 'PASS（讲解无卡顿、识图不再超时）')
process.exit(fails.length ? 1 : 0)
