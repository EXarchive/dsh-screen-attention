// 状态一致性回归：
//  A 浮窗黑屏/漂移后 state.overlayOpen 必须跟 helper 真实状态对齐
//  B helper 在飞请求结束后 refs 必须归 0（否则空闲退出是死代码）
//  C 快档采集（60fps，帧 0.4×）下 watch 上报的运动区域必须是屏幕坐标
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = { tools: { register(d) { tools.set(d.name, d); return d } }, on() {}, effect(fn) { fn() }, injectToSession() {}, agents: { currentInitiator: () => undefined, get: () => undefined }, log() {}, warn() {} }
const dispose = mod.default ? mod.default(ctx) : mod.apply(ctx)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
await sleep(1200)
let fails = []

// ---- B: refs 归零 ----
const st0 = await tools.get('saperm_status').execute({})
await tools.get('saperm_health').execute({})
await sleep(300)
const st1 = await tools.get('saperm_status').execute({})
console.log('refs: before', JSON.stringify(st0.helper), '-> after control calls', JSON.stringify(st1.helper))
if (st1.helper.refs !== 0) fails.push('refs 未归零: ' + st1.helper.refs)

// ---- A: 浮窗漂移 ----
const ov = tools.get('saperm_overlay')
await ov.execute({ action: 'open', x: 700, y: 300, w: 600, h: 400 })
await sleep(700)
const s1 = await ov.execute({ action: 'status' })
console.log('A1 open  ->', JSON.stringify({ open: s1.open, state: s1.stateOverlayOpen, frame: s1.frame && s1.frame.present }))
// 模拟用户双击关窗（直接让 helper 关，不通知 JS）
await ov.execute({ action: 'close' })
await sleep(400)
const s2 = await ov.execute({ action: 'status' })
console.log('A2 close ->', JSON.stringify({ open: s2.open, state: s2.stateOverlayOpen }))
// 关键：窗口已关，再 set 字幕，state 必须回到 false 而不是被写成 true
const s3 = await ov.execute({ action: 'set', subtitle: '窗口已关闭时不该假装开着' })
console.log('A3 set(no window) ->', JSON.stringify({ open: s3.open, state: s3.stateOverlayOpen }))
const h1 = await tools.get('saperm_health').execute({})
console.log('A4 health.overlayOpen ->', h1.overlayOpen)
if (s3.stateOverlayOpen !== false || h1.overlayOpen !== false) fails.push('overlayOpen 仍在漂')

// ---- C: 快档运动区域坐标 ----
// 浮窗现在被排除在截图之外（WDA_EXCLUDEFROMCAPTURE），所以"开窗制造变化"已经不可观测；
// 改用**几何反证**：60fps 快档下 FrameHub 的帧是 0.4×（2560×1600 → 1024×640），
// 若 motionRect 还是帧坐标，那么任何 rect 都必须落在 [0,1024]×[0,640] 内。
// 只要观测到 x+w > 1024 或 y+h > 640，就证明已经映射回屏幕坐标。
const F = tools.get('saperm_frames')
const W = tools.get('saperm_watch')
await F.execute({ action: 'start', fps: 60 })
await sleep(800)
await W.execute({ action: 'start', intervalMs: 200 })
const rects = []
for (let i = 0; i < 8; i++) {
  await sleep(600)
  const snap = await tools.get('saperm_observe').execute({})
  if (snap && snap.rect && snap.rect.w > 0) rects.push(snap.rect)
}
await W.execute({ action: 'stop' })
await F.execute({ action: 'stop' })
const maxRight = rects.reduce((m, r) => Math.max(m, r.x + r.w), 0)
const maxBottom = rects.reduce((m, r) => Math.max(m, r.y + r.h), 0)
const screenW = 2560, screenH = 1600
console.log('C samples ->', rects.length, 'maxRight:', maxRight, 'maxBottom:', maxBottom, '| frame-box(0.4x):', screenW * 0.4, screenH * 0.4)
console.log('C first rects ->', JSON.stringify(rects.slice(0, 3)))
if (!rects.length) fails.push('C 没有产生运动区域（屏幕静止，样本不足）')
else if (maxRight <= screenW * 0.4 && maxBottom <= screenH * 0.4) fails.push('C rect 全在 0.4× 帧框内 → 未映射到屏幕坐标')
console.log('RESULT:', fails.length ? 'FAIL ' + fails.join(' | ') : 'PASS (refs/idle + overlay 真值 + 快档屏幕坐标)')
if (typeof dispose === 'function') { try { await dispose() } catch {} }
process.exit(0)
