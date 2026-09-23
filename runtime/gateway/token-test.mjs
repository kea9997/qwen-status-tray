import fs from 'node:fs/promises';
import path from 'node:path';
import {randomUUID} from 'node:crypto';
import {readStream} from '../worker/stream-response.mjs';
import {metrics,aggregate} from './token-metrics.mjs';
import {readConfig,readBackend} from './runtime-config.mjs';
import {BENCHMARK_OUTPUT_TOKENS,CONTEXT_TARGETS} from './context-limits.mjs';
const config=readConfig(),backend=await readBackend(config),root=config.gatewayRoot,profile=process.argv[2]||'quick';
if(!['quick','8k','32k','64k','224k'].includes(profile))throw Error('Profile: quick|8k|32k|64k|224k');
const base=config.queueUrl, controller=new AbortController();
process.stdin.setEncoding('utf8');process.stdin.on('data',s=>{if(s.includes('cancel'))controller.abort(Error('사용자 취소'));});
if(process.argv.includes('--ui'))process.stdin.on('end',()=>controller.abort(Error('실행 창 연결 종료')));
const report={profile,started_at:new Date().toISOString(),rows:[],notes:'클라이언트 측 스트리밍 측정. 대기열/HTTP 포함. decode는 청크 기반 추정이며 실제 토큰별 지연이 아님. 워밍업 제외. 캐시 초기화 없음. 64K는 입력+출력 합계 한도.'};
const dir=path.join(root,'token-tests'),id=Date.now()+'-'+randomUUID().slice(0,8),file=path.join(dir,id+'.json');
let lock;
const log=s=>console.log(s), fmt=n=>n===null?'—':n.toFixed(2);
async function json(url,options={}){const r=await fetch(url,{...options,signal:AbortSignal.any([controller.signal,AbortSignal.timeout(profile==='224k'?60000:15000)])});if(!r.ok)throw Error('HTTP '+r.status+' '+(await r.text()).slice(0,300));return r.json();}
async function idle(){let q=await json(base+'/queue');if(q.running&&!q.waiting&&!q.ui_running&&report.rows.at(-1)?.status==='completed'){await new Promise(r=>setTimeout(r,250));q=await json(base+'/queue');}if(q.running||q.waiting||q.ui_running)throw Error('다른 요청이 사용 중입니다. 대기열이 비면 다시 실행하세요.');}
async function save(){report.aggregate=aggregate(report.rows);await fs.writeFile(file,JSON.stringify(report,null,2));await fs.writeFile(path.join(dir,id+'.txt'),report.notes+'\n\n'+report.rows.map(r=>r.name+': '+r.status+' | 입력 '+r.input_tokens+' / 출력 '+r.output_tokens+' | '+fmt(r.effective_tok_s??null)+' tok/s | '+(r.error||'')).join('\n')+'\n\n'+JSON.stringify(report.aggregate)+'\n'+(report.error||''));}
async function run(name,prompt,max_tokens,warmup=false,expected=null,qualityOnly=false){
  await idle();const row={name,warmup,quality_only:qualityOnly,status:'running'};report.rows.push(row);await save();log(name+' 시작…');
  const started=performance.now();let first=null;
  try{
    const r=await fetch(base+'/v1/chat/completions',{method:'POST',headers:{'content-type':'application/json','x-qwen-source':'token-test'},signal:AbortSignal.any([controller.signal,AbortSignal.timeout(backend==='ninfer'?1800000:240000)]),body:JSON.stringify({model:config.model,messages:[{role:'user',content:prompt}],max_tokens,temperature:0,stream:true,stream_options:{include_usage:true}})});
    row.request_id=r.headers.get('x-qwen-request-id');if(!r.ok)throw Error('HTTP '+r.status+' '+(await r.text()).slice(0,300));
    const data=await readStream(r.body,async()=>{},()=>{first=(performance.now()-started)/1000;});
    Object.assign(row,metrics(data.usage?.prompt_tokens,data.usage?.completion_tokens,(performance.now()-started)/1000,first));
    row.output=data.choices[0].message.content;row.finish_reason=data.choices[0].finish_reason;
    if(row.input_tokens===null||row.output_tokens===null)throw Error('서버 usage 토큰 수 누락');
    row.status='completed';row.recall_pass=expected?expected.every(v=>row.output.includes(v)):null;
    log(name+': 입력 '+row.input_tokens+' / 출력 '+row.output_tokens+' | 전체 '+fmt(row.effective_tok_s)+' tok/s | 첫 응답 '+fmt(first)+'초 | 요청 '+fmt(row.request_seconds)+'초 | 생성 추정 '+fmt(row.decode_tok_s)+' tok/s'+(expected?' | 정보 찾기 '+(row.recall_pass?'통과':'실패'):'')+(row.finish_reason==='length'?' | 출력 한도 도달':''));
  }catch(e){row.status=controller.signal.aborted?'cancelled':'failed';row.error=e.message;throw e;}finally{await save();}
}
async function contextPrompt(target){
  const nonce=randomUUID(), values=[0,1,2].map(()=>randomUUID());
  function make(n){const lines=Array.from({length:n},(_,i)=>'record '+i+': ordinary blue river archive data.');for(let j=0;j<3;j++)lines[Math.floor(n*[.1,.5,.9][j])]='SECRET_KEY_'+j+'='+values[j];return 'Run '+nonce+'\nRead the records below. Return only the exact values of SECRET_KEY_0, SECRET_KEY_1, SECRET_KEY_2.\n'+lines.join('\n')+'\nReturn the three key values now.';}
  let lo=10,hi=Math.ceil(target/3),best=null,count=0;
  while(lo<=hi){const mid=Math.floor((lo+hi)/2),prompt=make(mid);const d=await json(base+'/tokenize',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({messages:[{role:'user',content:prompt}]})});if(!Number.isInteger(d.count))throw Error('토큰 계산 실패');if(d.count<=target){best=prompt;count=d.count;lo=mid+1;}else hi=mid-1;}
  if(!best||count<target*.98)throw Error('목표 문맥 길이를 만들지 못했습니다.');log('실제 입력 준비: '+count+' 토큰');return {prompt:best,values};
}
try{
  await fs.mkdir(dir,{recursive:true});
  // OS exclusive file creation prevents simultaneous runners; a crash leaves an explicit recoverable lock.
  try{lock=await fs.open(path.join(dir,'running.lock'),'wx');await lock.writeFile(String(process.pid));}catch{throw Error('토큰 테스트 실행 잠금이 있습니다. 실행 중인 테스트를 종료하거나, 프로세스 종료 확인 후 token-tests/running.lock을 제거하세요.');}
  if(profile==='224k'&&backend!=='ninfer')throw Error('224K 검사는 ninfer 모드에서만 가능합니다.');
  await json(base+'/health');await idle();log('서버 준비 확인 · '+profile+' 테스트');
  await run('워밍업','Run '+randomUUID()+': Write the numbers one through ten.',32,true);
  if(profile==='quick'){
    for(const [i,p] of ['Explain FIFO request queues with advantages and limitations in about 250 words.','Write a Python merge_intervals function and explain empty, overlapping and touching intervals.','한국어로 LLM의 입력 토큰, 출력 토큰, 첫 응답 시간, 캐시를 예시와 함께 설명해줘.'].entries())await run('속도 '+(i+1),'Run '+randomUUID()+'\n'+p,384);
    const marker=randomUUID().slice(0,8);await run('정확성 검사','Return only these two facts from the text: 17 plus 25 equals 42; marker is '+marker+'. Format exactly: 42 '+marker,64,false,['42',marker],true);
  }else{const target=CONTEXT_TARGETS[profile];const c=await contextPrompt(target);await run(profile+' 문맥',c.prompt,BENCHMARK_OUTPUT_TOKENS,false,c.values);}
  if(report.rows.some(r=>r.recall_pass===false))throw Error('요청 처리는 완료됐지만 문맥 정보 찾기 검사에 실패했습니다.');
  report.status='completed';log('완료 · 유효 '+fmt(aggregate(report.rows).effective_tok_s)+' tok/s (워밍업 제외)');
}catch(e){report.status=controller.signal.aborted?'cancelled':'failed';report.error=e.message;log('중단: '+e.message);process.exitCode=1;}
finally{if(lock){await save();await lock.close();await fs.unlink(path.join(dir,'running.lock'));log('보고서: '+file);}process.stdin.pause();}
