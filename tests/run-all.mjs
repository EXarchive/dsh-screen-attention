// 一键回归：顺序跑全部插件级测试（每个用例自己起 helper，必须串行）
// 用法: node tests/run-all.mjs [--list]
import { spawnSync } from 'node:child_process'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const SUITE = [
  ['plugin-smoke', '47 工具注册 / health / infer↔interpret 互斥'],
  ['prod-readiness', '投产自检 21 项（watchdog 自愈、角色备注迁移、诊断字段、保留清理）'],
  ['state-drift-test', 'refs 归零 / overlay 状态对齐 / 快档屏幕坐标'],
  ['thread-gen-test', '采集线程世代号（停后立刻启不双线程）'],
  ['shared-buffer-smoke', '60fps 采集下 7 个慢消费者'],
  ['narrate-budget', '短窗口讲解（首轮不计入预算）'],
  ['vision-probe', 'saperm_vision 识图（零副作用）'],
]
if (process.argv.includes('--list')) {
  for (const [n, d] of SUITE) console.log(n.padEnd(22) + d)
  process.exit(0)
}

const results = []
for (const [name, desc] of SUITE) {
  const t0 = Date.now()
  console.log('\n=== ' + name + ' — ' + desc + ' ===')
  const r = spawnSync(process.execPath, [join(here, name + '.mjs')], { stdio: 'inherit', cwd: join(here, '..') })
  results.push({ name, code: r.status, ms: Date.now() - t0 })
}
console.log('\n===== SUMMARY =====')
for (const r of results) console.log((r.code === 0 ? 'PASS ' : 'FAIL ') + r.name.padEnd(22) + Math.round(r.ms / 1000) + 's' + (r.code === 0 ? '' : ' (exit ' + r.code + ')'))
const failed = results.filter(r => r.code !== 0)
console.log(failed.length ? 'RESULT: ' + failed.length + ' FAILED' : 'RESULT: all ' + results.length + ' suites passed')
process.exit(failed.length ? 1 : 0)
