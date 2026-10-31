// AI 可读数据流（stream）专项测试
const mod = await import('file:///C:/Users/Administrator/PycharmProjects/dsh-screen-attention/permission-plugin/lib/index.js')
const tools = new Map()
const ctx = {
  tools: { register(d) { tools.set(d.name, d); return d } },
  on() {}, effect(fn) { fn() },
  get: () => undefined,
  agents: { currentInitiator: () => undefined, get: () => undefined },
  log() {}, warn() {},
}
mod.apply(ctx)
await new Promise(r => setTimeout(r, 900))

let pass = 0
const ok = (n, c, d) => { if (!c) { console.log('FAIL ' + n + ' -> ' + JSON.stringify(d)); process.exitCode = 1 } else { pass++; console.log('PASS ' + n + (d !== undefined ? ' -> ' + d : '')) } }

ok('工具已注册 saperm_stream', !!tools.get('saperm_stream'))

// 制造真实屏幕变化的小窗
await tools.get('saperm_overlay_open').execute({ x: 300, y: 260, w: 420, h: 260 })
await tools.get('saperm_infer_start').execute({ maxBoxes: 40, objects: true, scene: true, accurate: false, animeface: true, sceneEvery: 4, faceEvery: 2, streamMs: 700 })

for (let i = 0; i < 5; i++) {
  await tools.get('saperm_overlay_set').execute({ note: 'stream probe ' + i + ' ' + Date.now() })
  await new Promise(r => setTimeout(r, 800))
}

const streamRes = await tools.get('saperm_stream').execute({ limit: 20 })
const statusRes = await tools.get('saperm_infer_status').execute({ limit: 1 })

ok('stream 有输出行', streamRes.count >= 2, 'count=' + streamRes.count)
ok('stream 行含窗口字段 W=', /W=/.test(streamRes.text || ''), (streamRes.text || '').split('\n')[0])
ok('stream 行含帧号 #', /#\d+/.test(streamRes.text || ''), '')
ok('infer_status 自带 stream 状态', !!(statusRes.stream && statusRes.stream.running), JSON.stringify(statusRes.stream))

// token 效率对比：紧凑行 vs 裸 infer_status JSON
const rawJson = JSON.stringify(statusRes)
const streamBytes = Buffer.byteLength(streamRes.text || '', 'utf8')
const perFrameRaw = Buffer.byteLength(rawJson, 'utf8')
ok('单帧数据流体积远小于裸 JSON', streamBytes > 0 && (streamBytes / Math.max(1, streamRes.count)) < perFrameRaw / 2,
  'perLine=' + Math.round(streamBytes / Math.max(1, streamRes.count)) + 'B vs rawJson=' + perFrameRaw + 'B')

const fileRes = await tools.get('saperm_stream').execute({ file: true })
ok('latest.txt 已生成', !!fileRes.file && fsExists(fileRes.file), fileRes.file)

function fsExists(p) { try { const fs = require('node:fs'); return fs.existsSync(p) } catch { return false } }

await tools.get('saperm_infer_stop').execute({})
await new Promise(r => setTimeout(r, 300))
const after = await tools.get('saperm_infer_stop').execute({}).catch(() => null)
const health = await tools.get('saperm_health').execute({})
ok('infer_stop 自动停流', health.stream && health.stream.running === false, JSON.stringify(health.stream))

console.log('\n===== 数据流样例 =====\n' + (streamRes.text || '').split('\n').slice(-8).join('\n'))
console.log('\nALL ' + pass + ' STREAM CHECKS PASSED')
