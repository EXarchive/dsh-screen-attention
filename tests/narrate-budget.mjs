// 短窗口回归：seconds:8 必须至少出一条，且首轮不计入预算
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = { tools: { register(d) { tools.set(d.name, d); return d } }, on() {}, effect(fn) { fn() }, injectToSession() {}, agents: { currentInitiator: () => undefined, get: () => undefined }, log() {}, warn() {} }
mod.default ? mod.default(ctx) : mod.apply(ctx)
await new Promise(r => setTimeout(r, 900))
const n = tools.get('saperm_narrate')
// thinking 模型单条 6-13s：用 25 秒窗口 + 8s 节奏验证"短窗口也能出条"
const s = await n.execute({ action: 'start', seconds: 25, periodMs: 6000 })
console.log('start:', JSON.stringify(s))
const polls = []
for (let i = 0; i < 16; i++) {
  await new Promise(r => setTimeout(r, 3000))
  const st = await n.execute({ action: 'status' })
  polls.push({ t: (i + 1) * 2, n: st.narrations, errs: st.errors, running: st.running })
  if (!st.running) break
}
console.log('polls:', JSON.stringify(polls))
const fin = await n.execute({ action: 'stop' })
console.log('stop:', JSON.stringify(fin))
const logs = tools.get('saperm_history')
const h = await logs.execute({ type: 'narrate', limit: 3 })
console.log('history narrate entries:', JSON.stringify((h.items || []).slice(0, 2)))
if (typeof dispose === 'function') { try { await dispose() } catch {} }
process.exit(0)
