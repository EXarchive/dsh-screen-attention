import{spawn}from'node:child_process'
import fs from 'node:fs'
const exe='C:/Users/Administrator/PycharmProjects/dsh-screen-attention/helper/bin/Release/net9.0-windows10.0.19041.0/SaHelper.exe'
const c=spawn(exe,[],{stdio:['pipe','pipe','pipe'],windowsHide:true});let b='';let id=1;const p=new Map()
c.stdout.on('data',d=>{b+=d.toString('utf8');let i;while((i=b.indexOf('\n'))>=0){const l=b.slice(0,i).trim();b=b.slice(i+1);if(!l)continue;let m;try{m=JSON.parse(l)}catch{continue}if(m.event)continue;const q=p.get(m.id);if(q){p.delete(m.id);q(m)}}})
c.stderr.on('data',()=>{})
const sleep=ms=>new Promise(r=>setTimeout(r,ms))
function call(cmd,a={}){const i=id++;return new Promise(r=>{p.set(i,r);c.stdin.write(JSON.stringify({id:i,cmd,...a})+'\n')})}
const dir='C:/Users/Administrator/PycharmProjects/dsh-screen-attention/tests/fill-data'
const files=fs.readdirSync(dir).filter(f=>/\.(jpe?g|png|bmp)$/i.test(f)).sort()
console.log('图片:', files.length)
// 1) 逐图 OCR 提取第一个可用文本作为内容
const rows=[]
for(const f of files){
  const fp=dir+'/'+f
  const t=Date.now(); const r=await call('ocr_file',{path:fp,scale:1}); const ms=Date.now()-t
  const items=(r.data&&r.data.items)||[]
  const cn=items.map(x=>x.text).filter(x=>/[\u4e00-\u9fa5]/.test(x))
  const content=(cn[0]||items[0]?.text||'').slice(0,20)
  rows.push({file:f,ms,items:items.length,content})
  console.log('  ',f, '|', ms+'ms |', items.length+'items |', content)
}
// 2) 写 Excel：找前台 Excel
let w=await call('windows'); let ex=(w.data.windows||[]).find(x=>x.cls==='XLMAIN')
if(!ex){ console.log('Excel 未运行，跳过写入'); c.stdin.write(JSON.stringify({id:0,cmd:'shutdown'})+'\n'); process.exit(0) }
await call('focus',{hwnd:ex.hwnd}); await sleep(400)
let ok=0
for(let i=0;i<rows.length;i++){
  const row=2+i
  const ref='A'+row
  await call('hotkey',{keys:['F5']}); await sleep(600)
  await call('type',{text:ref}); await sleep(250)
  await call('key',{name:'ENTER'}); await sleep(500)
  await call('clipboard_set',{text:rows[i].file+'\t'+rows[i].content})
  await call('hotkey',{keys:['CTRL','V']}); await sleep(400)
  // 回读
  await call('hotkey',{keys:['F5']}); await sleep(500)
  await call('type',{text:ref+':B'+row}); await sleep(200)
  await call('key',{name:'ENTER'}); await sleep(400)
  await call('hotkey',{keys:['CTRL','C']}); await sleep(600)
  const cg=await call('clipboard_get')
  const got=String((cg.data&&cg.data.text)||'').replace(/[\r\n]+$/,'')
  const exp=rows[i].file+'\t'+rows[i].content
  const match=got===exp
  if(match) ok++
  console.log('  row'+row+':', match?'✓':'✗', '| 期望:', JSON.stringify(exp), '| 回读:', JSON.stringify(got))
}
console.log('写入校验: '+ok+'/'+rows.length)
c.stdin.write(JSON.stringify({id:0,cmd:'shutdown'})+'\n')