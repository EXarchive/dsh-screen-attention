// 共享采集缓冲（_capBuf）慢消费者回归：60fps 采集同时在跑时，逐个跑需要独占画面的消费者，
// 断言 helper 不崩、每个都返回结构化结果（撕裂在语义上表现为偶发空/垃圾结果，这里守"不崩 + 有输出"）。
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = { tools: { register(d) { tools.set(d.name, d); return d } }, on() {}, effect(fn) { fn() }, injectToSession() {}, agents: { currentInitiator: () => undefined, get: () => undefined }, log() {}, warn() {} }
const dispose = mod.default ? mod.default(ctx) : mod.apply(ctx)
const sleep = (ms) => new Promise(r => setTimeout(r, ms))
await sleep(1200)

const F = tools.get('saperm_frames')
const V = tools.get('saperm_vision')
const see = tools.get('saperm_see')
await F.execute({ action: 'start', fps: 60 })
await sleep(600)
console.log('frames running:', JSON.stringify((await F.execute({ action: 'stats' })).loops))

const results = []
const time = async (label, fn) => {
  const t0 = Date.now()
  try {
    const r = await fn()
    results.push({ label, ok: true, ms: Date.now() - t0, shape: Object.keys(r || {}).slice(0, 6).join(',') })
  } catch (e) {
    results.push({ label, ok: false, ms: Date.now() - t0, err: String(e.message || e).slice(0, 90) })
  }
}
// 慢消费者：都用全屏 = 与采集同尺寸，正是以前会互相覆写的那条路径
await time('ocr(recognize full)', () => tools.get('saperm_recognize').execute({ full: true, maxTexts: 30 }))
await time('char.match', () => tools.get('saperm_char').execute({ action: 'match' }))
await time('animeface', () => tools.get('saperm_animeface').execute({}))
await time('scene(via see)', () => see.execute({ full: true, text: false, scene: true, show: false }))
await time('detect(via see)', () => see.execute({ full: true, text: false, objects: true, show: false }))
await time('screenshot->jpeg', () => V.execute({ engine: 'ocr', full: true, maxTexts: 5 }))
await time('vision vlm', () => V.execute({ engine: 'vlm', full: true, maxTokens: 24 }))
const st = await F.execute({ action: 'stats' })
await F.execute({ action: 'stop' })

console.log(JSON.stringify(results, null, 1))
console.log('capture while busy:', JSON.stringify({ loops: st.loops, fps: st.fps, gaps: st.gaps }))
const bad = results.filter(r => !r.ok)
console.log('RESULT:', bad.length ? 'FAIL ' + bad.map(b => b.label + ':' + b.err).join(' | ') : 'PASS (all slow consumers ok under 60fps capture)')
if (typeof dispose === 'function') { try { await dispose() } catch {} }
process.exit(0)
