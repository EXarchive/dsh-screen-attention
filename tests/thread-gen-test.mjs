// 采集线程世代号回归：stop→立刻 start 之后再怎么折腾，采集循环必须只有一个（stats.loops === 1）
// 用法: node tests/thread-gen-test.mjs
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = { tools: { register(d) { tools.set(d.name, d); return d } }, on() {}, effect(fn) { fn() }, injectToSession() {}, agents: { currentInitiator: () => undefined, get: () => undefined }, log() {}, warn() {} }
const dispose = mod.default ? mod.default(ctx) : mod.apply(ctx)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
await sleep(1200)

const F = tools.get('saperm_frames')
const W = tools.get('saperm_watch')
const I = tools.get('saperm_interpret')

let fails = []
// 1) frames：快速 stop/start 5 轮，最后保持运行，断言 loops 只有一个
for (let i = 0; i < 5; i++) {
  await F.execute({ action: 'start', fps: 60 })
  await sleep(250)
  await F.execute({ action: 'stop' })
  await F.execute({ action: 'start', fps: 60 })  // 停完立刻起（这就是原来的竞态窗口）
}
await sleep(1200)
const st = await F.execute({ action: 'stats' })
console.log('frames after 5x rapid restart:', JSON.stringify({ loops: st.loops, fps: st.fps, frames: st.frames, gaps: st.gaps }))
if (st.loops !== 1) fails.push('frames.loops=' + st.loops)
await F.execute({ action: 'stop' })
await sleep(300)
const st2 = await F.execute({ action: 'stats' })
console.log('after frames stop:', JSON.stringify({ loops: st2.loops, running: st2.running }))
if (st2.loops !== 0) fails.push('frames.stop 后 loops=' + st2.loops)

// 2) watch 快速 stop/start，并验证快照仍能出帧
for (let i = 0; i < 3; i++) {
  await W.execute({ action: 'start', intervalMs: 200 })
  await sleep(250)
  await W.execute({ action: 'stop' })
}
await W.execute({ action: 'start', intervalMs: 200 })
await sleep(1500)
const obs = await tools.get('saperm_observe').execute({})
console.log('watch after 3x rapid restart: observe ->', JSON.stringify(obs).slice(0, 160))
await W.execute({ action: 'stop' })

// 3) interpret 快速 stop/start
for (let i = 0; i < 3; i++) {
  const s = await I.execute({ action: 'start', intervalMs: 400 })
  await sleep(250)
  const p = await I.execute({ action: 'stop' })
}
const fin = await I.execute({ action: 'start', intervalMs: 400 })
await sleep(1200)
const ist = await I.execute({ action: 'status' })
await I.execute({ action: 'stop' })
console.log('interpret after 3x rapid restart:', JSON.stringify({ running: ist.running, ocrRuns: ist.ocrRuns, lastError: ist.lastError }))
if (!ist.running) fails.push('interpret 未能重启')

console.log('RESULT:', fails.length ? 'FAIL ' + fails.join(', ') : 'PASS (single capture loop, restarts clean)')
if (typeof dispose === 'function') { try { await dispose() } catch {} }
process.exit(0)
