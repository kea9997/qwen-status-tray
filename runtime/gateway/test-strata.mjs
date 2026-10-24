import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import http from 'node:http';
import {createGateway} from './gateway.mjs';
import {countTokens} from './backend-adapter.mjs';
import {readConfig,readBackend} from './runtime-config.mjs';
import {strataIdentity,normalizeStrataMetrics,strataCountPayload} from './strata.mjs';
import {chatInputBudget,benchmarkProfiles} from './context-limits.mjs';
import {readStream} from '../worker/stream-response.mjs';

const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-strata-test-'));
const model='fixture-sc117-flash-next-iq3_xxs';
let loaded=true,context=32768,served=model,tokenMode='count',tokenValue=5,redirect=false,generationCalls=0,countCalls=0;
let lastGeneration,lastCount,metricsMode='ok',images=false;
const metrics={engine:{model,max_context:32768},live:{state:'idle',queued:0,tok_s:null,tok_s_mean:null,prefill_tok_s_mean:null},totals:{since:100,prompt_tokens:10,output_tokens:7}};
const events=[
 {choices:[{delta:{tool_calls:[{index:0,id:'call-fixture',type:'function',function:{name:'read_file',arguments:'{"path":'}}]},finish_reason:null}]},
 {choices:[{delta:{tool_calls:[{index:0,function:{arguments:'"한글.txt"}'}}]},finish_reason:null}]},
 {choices:[{delta:{},finish_reason:'tool_calls'}]},
 {choices:[],usage:{prompt_tokens:11,completion_tokens:7,total_tokens:18},timings:{prompt_n:11,predicted_n:7,prompt_per_second:123,predicted_per_second:45}},
];
const wire=events.map(e=>'data: '+JSON.stringify(e)+'\r\n\r\n').join('')+'data: [DONE]\r\n\r\n';
const fake=http.createServer(async(req,res)=>{
 res.setHeader('content-type','application/json');
 if(redirect){res.writeHead(302,{location:'/must-not-follow'});return res.end();}
 if(req.url==='/health')return res.end(JSON.stringify({service:'strata',loaded,model:served,max_context:context,images}));
 if(req.url==='/v1/models')return res.end(JSON.stringify({data:[{id:served,meta:{n_ctx:context},architecture:{input_modalities:images?['text','image']:['text']}}]}));
 if(req.url==='/metrics'){
  if(metricsMode==='failed'){res.writeHead(503);return res.end('{}');}
  if(metricsMode==='malformed')return res.end('{');
  if(metricsMode==='foreign')return res.end('{}');
  return res.end(JSON.stringify(metrics));
 }
 let raw='';for await(const chunk of req)raw+=chunk;const body=JSON.parse(raw);
 const text=body.messages?.map(m=>typeof m.content==='string'?m.content:JSON.stringify(m.content)).join('')||'';
 const tokens=Number(text.match(/count=(\d+)/)?.[1]??tokenValue);
 if(req.url==='/tokenize'){
  countCalls++;lastCount=body;
  if(tokenMode==='fallback'){res.writeHead(404);return res.end('{}');}
  if(tokenMode==='tokens')return res.end(JSON.stringify({tokens:Array(tokens).fill(1)}));
  return res.end(JSON.stringify({count:tokenMode==='invalid'?null:tokens}));
 }
 if(req.url==='/v1/messages/count_tokens'){countCalls++;lastCount=body;return res.end(JSON.stringify({input_tokens:tokens}));}
 assert.equal(req.url,'/v1/chat/completions');generationCalls++;lastGeneration=body;
 res.setHeader('content-type','text/event-stream');
 const result=body.tools?wire:'data: '+JSON.stringify({choices:[{delta:{content:'fixture answer'},finish_reason:'stop'}]})+'\n\ndata: '+JSON.stringify({choices:[],usage:{prompt_tokens:tokens,completion_tokens:2}})+'\n\ndata: [DONE]\n\n';
 const bytes=Buffer.from(result);for(let i=0;i<bytes.length;i+=7)res.write(bytes.subarray(i,i+7));res.end();
});
await new Promise(r=>fake.listen(0,'127.0.0.1',r));const upstream='http://127.0.0.1:'+fake.address().port;
process.env.QWEN_BACKEND='strata';process.env.QWEN_MODEL_ID=model;delete process.env.QWEN_BACKEND_FILE;delete process.env.QWEN_CONTEXT_TOKENS;
const gateway=await createGateway({port:0,upstream,root});const base='http://127.0.0.1:'+gateway.address().port;
const messages=[{role:'user',content:'hello'}],headers={'x-ui-token':(await fs.readFile(path.join(root,'ui-token.txt'),'utf8')).trim()};
const submit=payload=>fetch(base+'/v1/chat/completions',{method:'POST',body:JSON.stringify({messages,stream:true,...payload})});
async function eventually(fn){for(let i=0;i<200;i++){const value=await fn();if(value)return value;await new Promise(r=>setTimeout(r,10));}throw Error('Strata fixture timeout');}
try{
 assert.throws(()=>readConfig({QWEN_BACKEND:'strata'}),/MODEL_ID/);
 assert.equal(readConfig({QWEN_BACKEND:'strata',QWEN_MODEL_ID:model}).model,model);
 const backendFile=path.join(root,'backend.txt');await fs.writeFile(backendFile,'strata');assert.equal(await readBackend({backendFile}), 'strata');
 assert.equal(chatInputBudget('vllm'),61440);assert.equal(chatInputBudget('ninfer'),225280);assert.equal(chatInputBudget('strata',32768),28672);
 assert.throws(()=>chatInputBudget('strata'),/capacity/);
 assert.deepEqual(benchmarkProfiles('strata',32768),['quick','8k','32k']);assert(!benchmarkProfiles('strata',65536).includes('224k'));assert(benchmarkProfiles('strata',262144).includes('224k'));
 const health={service:'strata',loaded:true,model,max_context:32768};
 assert.equal(strataIdentity(health,{data:[{id:model,meta:{n_ctx:16384}}]},model).contextTokens,16384);
 assert.throws(()=>strataIdentity({...health,max_context:null},null,model),/unknown/);
 assert.throws(()=>strataIdentity(health,{data:[]},model),/not listed/);
 const caps=await(await fetch(base+'/capabilities')).json();assert.equal(caps.model,model);assert.equal(caps.context_tokens,32768);assert.equal(caps.vision_ready,false);assert(!caps.benchmark_profiles.includes('64k'));
 images=true;assert.equal((await(await fetch(base+'/capabilities')).json()).vision_ready,false);
 const beforeImage=generationCalls,beforeImageCount=countCalls;
 const imageResponse=await submit({messages:[{role:'user',content:[{type:'text',text:'describe'},{type:'image_url',image_url:{url:'data:image/png;base64,aGVsbG8='}}]}]});
 assert.equal(imageResponse.status,400);assert.match(await imageResponse.text(),/텍스트 전용/);assert.equal(generationCalls,beforeImage);assert.equal(countCalls,beforeImageCount);images=false;
 loaded=false;assert.equal((await fetch(base+'/health')).status,503);assert.equal((await submit({})).status,503);assert.equal(generationCalls,0);loaded=true;
 assert.equal((await countTokens({upstream,backend:'strata',model,messages})),5);
 tokenMode='tokens';assert.equal(await countTokens({upstream,backend:'strata',model,messages}),5);
 tokenMode='invalid';await assert.rejects(countTokens({upstream,backend:'strata',model,messages}),/Invalid token count/);
 tokenMode='fallback';assert.equal(await countTokens({upstream,backend:'strata',model,messages:[{role:'system',content:'rules'},...messages]}),5);assert.equal(lastCount.system,'rules');assert.equal(lastCount.thinking.type,'disabled');
 const converted=strataCountPayload(model,[{role:'assistant',content:null,tool_calls:[{id:'c1',type:'function',function:{name:'f',arguments:'{"x":1}'}}]},{role:'tool',tool_call_id:'c1',content:'answer'}]);
 assert.equal(converted.messages[0].content[0].input.x,1);assert.equal(converted.messages[1].content[0].tool_use_id,'c1');tokenMode='count';
 assert.equal(normalizeStrataMetrics(metrics).output_tok_s,null);assert.equal(normalizeStrataMetrics(metrics).running,0);
 metrics.live={state:'generating',queued:2,tok_s:43,tok_s_mean:42,prefill_tok_s_mean:123};
 const normalized=await(await fetch(base+'/status')).json();assert.equal(normalized.output_tok_s,42);assert.equal(normalized.input_tok_s,123);assert.equal(normalized.waiting,2);assert.equal(normalized.running,1);
 assert.match(await(await fetch(base+'/metrics')).text(),/vllm:generation_tokens_total 7/);
 for(const mode of ['failed','malformed','foreign']){
  metricsMode=mode;const result=await fetch(base+'/metrics',{signal:AbortSignal.timeout(4000)});assert.notEqual(result.status,200);assert((await result.text()).length>0);
 }metricsMode='ok';
 metrics.live={state:'unloaded',queued:0};assert.equal(normalizeStrataMetrics(metrics).loaded,false);assert.equal(normalizeStrataMetrics(metrics).output_tok_s,null);
 metrics.totals={since:200,prompt_tokens:0,output_tokens:0};assert.equal(normalizeStrataMetrics(metrics).output_tokens,0);assert.equal(normalizeStrataMetrics({...metrics,totals:{}}).output_tokens,null);
 const tools=[{type:'function',function:{name:'read_file',parameters:{type:'object',properties:{path:{type:'string'}}}}}];
 const response=await submit({tools,model:'must-not-serve-this'});assert.equal(response.status,200);assert.equal(await response.text(),wire);assert.equal(lastGeneration.model,model);assert.equal(lastGeneration.reasoning_effort,'none');assert.deepEqual(lastGeneration.tools,tools);
 const requestId=response.headers.get('x-qwen-request-id');const record=await eventually(async()=>{const d=JSON.parse(await fs.readFile(path.join(root,'requests',requestId+'.json'),'utf8'));return d.finished_at?d:null;});
 assert.equal(record.backend,'strata');assert.equal(record.model,model);assert.equal(record.usage.completion_tokens,7);assert.equal(record.timings.predicted_per_second,45);assert.match(await fs.readFile(path.join(root,'requests',requestId+'.md'),'utf8'),/한글.txt/);
 const parsed=await readStream((async function*(){const b=Buffer.from(wire);for(let i=0;i<b.length;i++)yield b.subarray(i,i+1);})(),async()=>{});
 assert.equal(parsed.choices[0].message.tool_calls[0].function.arguments,'{"path":"한글.txt"}');assert.equal(parsed.timings.prompt_per_second,123);
 const atBoundary=await submit({messages:[{role:'user',content:'count=28672'}]});assert.equal(atBoundary.status,200);await atBoundary.text();
 const before=generationCalls;const overflow=await submit({messages:[{role:'user',content:'count=28673'}]});assert.equal(overflow.status,400);assert.equal(generationCalls,before);
 const started=await fetch(base+'/ui/jobs',{method:'POST',headers,body:JSON.stringify({mode:'chat',session:'context',prompt:'count=28672'})});assert.equal(started.status,200);const {id}=await started.json();
 const job=await eventually(async()=>{const d=await(await fetch(base+'/ui/jobs/'+id,{headers})).json();return d.finished_at?d:null;});assert.equal(job.status,'completed',job.error);assert.equal(job.context_tokens,28672);
 served='different-model';assert.equal((await fetch(base+'/health')).status,503);served=model;
 context=null;assert.equal((await fetch(base+'/health')).status,503);context=32768;
 redirect=true;await assert.rejects(countTokens({upstream,backend:'strata',model,messages}),/fetch failed/);assert.equal((await fetch(base+'/health')).status,503);redirect=false;
 assert(countCalls>0);console.log('PASS: fake Strata only; loadedfalse, identity/context, count/token-array/fallback, JSON/native/reset and failed/malformed metrics, text-only image refusal, overflow before generation, fragmented UTF8 tool SSE/usage/timings, no redirect, unavailable');
}finally{
 gateway.closeAllConnections();fake.closeAllConnections();await Promise.all([gateway,fake].map(s=>new Promise(r=>s.close(r))));
 assert.equal(path.dirname(root),os.tmpdir());assert(path.basename(root).startsWith('qwen-strata-test-'));await fs.rm(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
