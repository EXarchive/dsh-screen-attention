// saperm_vision 回归：只识别、零副作用（不开浮窗、不推送、不启动采集）
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = {
  tools: { register(def) { tools.set(def.name, def); return def } },
  on() {}, effect(fn) { fn() }, injectToSession() {},
  agents: { currentInitiator: () => undefined, get: () => undefined },
  log() {}, warn() {},
}
const dispose = mod.default ? mod.default(ctx) : mod.apply(ctx)
await new Promise(r => setTimeout(r, 900))

const v = tools.get('saperm_vision')
if (!v) { console.log('FAIL: saperm_vision 未注册'); process.exit(1) }
console.log('registered tools:', tools.size, '| vision desc has untrusted note:', /不可信/.test(v.description))

// 1) 参数校验
let bad = 'no-throw'
try { await v.execute({ engine: 'nope' }) } catch (e) { bad = e.message }
console.log('1 invalid engine ->', bad)

// 2) 纯 OCR（全屏）：不落盘副作用之外什么都不该做
const t1 = Date.now()
const r1 = await v.execute({ engine: 'ocr', full: true, maxTexts: 8 })
console.log('2 ocr  ->', JSON.stringify({ count: r1.ocr && r1.ocr.count, ms: r1.ms, within2s: r1.within2s, shown: r1.shown, injected: r1.injected, text: r1.text, sample: (r1.ocr && r1.ocr.items.slice(0, 3).map(i => i.text)) }))

// 3) 纯 VLM（喂图片文件，不抓屏）
const fs = await import('node:fs')
const dir = 'C:\\Users\\Administrator\\PycharmProjects\\dsh-screen-attention\\out\\sa-view'
let file = ''
try {
  const f = fs.readdirSync(dir).filter(n => /\.jpg$/i.test(n)).map(n => ({ n, t: fs.statSync(dir + '\\' + n).mtimeMs })).sort((a, b) => b.t - a.t)[0]
  if (f) file = dir + '\\' + f.n
} catch {}
console.log('3 file:', file ? file.slice(-40) : '(none)')
if (file) {
  const r3 = await v.execute({ engine: 'vlm', path: file, prompt: '一句话描述这张图' })
  console.log('3 vlm  ->', JSON.stringify({ text: r3.text, ms: r3.ms, size: r3.size, model: r3.model, tokens: r3.tokens, degraded: r3.degraded, within2s: r3.within2s }))
}

// 4) both（屏幕抓取 + 两条引擎）
const r4 = await v.execute({ engine: 'both', full: true, maxTexts: 5, prompt: '一句话描述画面' })
console.log('4 both ->', JSON.stringify({ text: r4.text, count: r4.ocr && r4.ocr.count, ms: r4.ms, rect: r4.rect, stored: !!r4.path }))

// 5) 副作用审计：全程不应留下浮窗/采集状态
const h = await tools.get('saperm_health').execute({})
console.log('5 health ->', JSON.stringify({ ok: h.ok, helper: h.helper, observation: h.observation, ollama: h.ollama && h.ollama.running }))

if (typeof dispose === 'function') { try { await dispose() } catch {} }
process.exit(0)