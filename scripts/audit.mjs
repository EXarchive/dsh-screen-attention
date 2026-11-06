#!/usr/bin/env node
// 仓库结构自检：把「类规模 / 接口一致性 / 空壳 / 死代码」一次性算准，
// 避免靠肉眼 + 不完整 grep 得出错误结论（2026-11-06 连续误判三次的产物）。
import fs from 'node:fs'
import path from 'node:path'

const ROOT = path.resolve(import.meta.dirname, '..')
const JS = path.join(ROOT, 'permission-plugin/lib/index.js')
const CS = path.join(ROOT, 'helper/Program.cs')
const csSrc = fs.readFileSync(CS, 'utf8')
const jsSrc = fs.readFileSync(JS, 'utf8')

// ── 1. 类规模：正确处理嵌套（按大括号深度，不按"下一个 class"）
const lines = csSrc.split(/\r?\n/)
const classes = []
let depth = 0, cur = null, startDepth = 0
for (let i = 0; i < lines.length; i++) {
  const m = lines[i].match(/^\s*(?:(?:public|private|internal|static|sealed|partial|abstract)\s+)*class\s+(\w+)/)
  if (m && cur === null) { cur = { name: m[1], start: i + 1, members: 0 }; startDepth = depth }
  if (cur) {
    if (/^\s{2,}(?:public|private|internal|static|readonly|volatile|[\w<>])[^;]*\([^)]*\)\s*$/.test(lines[i])) cur.members++
  }
  for (const ch of lines[i]) { if (ch === '{') depth++; else if (ch === '}') depth-- }
  if (cur && depth === startDepth && i > cur.start - 1 && lines[i].trim() === '}') {
    cur.end = i + 1; cur.size = cur.end - cur.start + 1; classes.push(cur); cur = null
  }
}
console.log('=== 类规模（按大括号深度，嵌套正确）===')
for (const c of classes.sort((a, b) => b.size - a.size)) {
  const flag = c.size > 400 ? ' ←大' : c.size < 10 ? ' ←空壳' : ''
  console.log(('  ' + c.name).padEnd(20) + String(c.size).padStart(5) + ' 行' + String(c.members).padStart(4) + ' 成员' + flag)
}

// ── 2. JS↔helper 接口一致性
const jsCalls = [...new Set([...jsSrc.matchAll(/helperCall\(\s*'([a-z_0-9]+)'/g)].map(m => m[1]))]
const csCases = [...new Set([...csSrc.matchAll(/^\s*case "([a-z_0-9]+)":/gm)].map(m => m[1]))]
const missing = jsCalls.filter(c => !csCases.includes(c))
const unused = csCases.filter(c => !jsCalls.includes(c) && c !== 'handshake' && c !== 'ping' && c !== 'shutdown')
console.log('\n=== 接口一致性 ===')
console.log('  JS 调用 ' + jsCalls.length + ' · helper case ' + csCases.length + ' · 双方都有 ' + (jsCalls.length - missing.length))
console.log('  ' + (missing.length ? '❌ 悬空: ' + missing.join(', ') : '✅ 无悬空调用'))
if (unused.length) console.log('  ⚠️ helper 独有: ' + unused.join(', '))

// ── 3. 空壳与死导出
const tools = [...new Set([...jsSrc.matchAll(/name: '(saperm_[a-z_]+)'/g)].map(m => m[1]))]
console.log('\n=== 规模 ===')
console.log('  工具 ' + tools.length + ' 个 · index.js ' + jsSrc.split(/\r?\n/).length + ' 行 · Program.cs ' + lines.length + ' 行')
const shells = classes.filter(c => c.members <= 2)
if (shells.length) console.log('  ⚠️ 空壳类: ' + shells.map(c => c.name + '(' + c.size + '行/' + c.members + '成员)').join(', '))
