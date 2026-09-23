import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import readline from 'node:readline';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {createGateway} from './gateway.mjs';
import {readConfig} from './runtime-config.mjs';
import {ConversationStore} from './conversation-store.mjs';

const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function eventually(fn,label){for(let i=0;i<250;i++){const result=await fn();if(result)return result;await wait(20);}throw Error('Timed out: '+label);}
const appRoot=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-portable-test-'));
const root=path.join(appRoot,'runtime','gateway');
let active=0,peak=0,online=true;
const payloads=[],children=[],release=new Map();
const backend=http.createServer(async(req,res)=>{
 if(req.url==='/health'){res.writeHead(online?200:503);return res.end();}
 if(req.url==='/v1/models')return res.end(JSON.stringify({data:[{id:'fixture-model'}]}));
 if(req.url==='/metrics')return res.end('fixture_requests_total 1\n');
 let raw='';for await(const b of req)raw+=b;const data=JSON.parse(raw);
 if(req.url==='/tokenize')return res.end(JSON.stringify({count:100}));
 assert.equal(req.url,'/v1/chat/completions');payloads.push(data);active++;peak=Math.max(peak,active);
 const prompt=data.messages.at(-1).content;
 const delay=prompt.includes('cancel-active')||prompt.includes('block-queue')?350:50;
 if(['fifo-first','block-queue'].includes(prompt))await new Promise(resolve=>{release.set(prompt,resolve);res.once('close',resolve);});
 else await new Promise(resolve=>{const timer=setTimeout(resolve,delay);res.once('close',()=>{clearTimeout(timer);resolve();});});
 active--;
 if(res.destroyed)return;
 const marker=prompt.match(/marker is ([a-f0-9]+)/)?.[1];
 const content=marker?'42 '+marker:'fixture answer';
 const usage={prompt_tokens:12,completion_tokens:7,total_tokens:19};
 if(data.stream){res.setHeader('content-type','text/event-stream');res.end('data: '+JSON.stringify({choices:[{delta:{content},finish_reason:'stop'}]})+'\n\ndata: '+JSON.stringify({choices:[],usage})+'\n\ndata: [DONE]\n\n');}
 else {res.setHeader('content-type','application/json');res.end(JSON.stringify({choices:[{message:{content},finish_reason:'stop'}],usage}));}
});
await new Promise(resolve=>backend.listen(0,'127.0.0.1',resolve));
const upstream='http://127.0.0.1:'+backend.address().port;
// All configured URLs and data directories are fixtures, including those inherited by child jobs.
const env={...process.env,QWEN_APP_ROOT:appRoot,QWEN_GATEWAY_DATA_DIR:root,QWEN_WORKER_DATA_DIR:path.join(appRoot,'runtime','worker'),QWEN_BACKEND:'vllm',QWEN_BACKEND_FILE:'',QWEN_MODEL_ID:'fixture-model',QWEN_UPSTREAM_URL:upstream,QWEN_HERMES_PYTHON:'',QWEN_HERMES_ROOT:'',QWEN_HERMES_HOME:'',QWEN_HERMES_GIT_BASH:'',QWEN_VISION_READY:'0'};
for(const key of Object.keys(env).filter(k=>k.startsWith('QWEN_')))process.env[key]=env[key];
const queue=await createGateway({port:0,root,upstream});
const base='http://127.0.0.1:'+queue.address().port;env.QWEN_QUEUE_URL=base;
const headers={'x-ui-token':(await fs.readFile(path.join(root,'ui-token.txt'),'utf8')).trim()};
const api=async(url,options={})=>{const response=await fetch(base+url,{...options,headers:{...headers,...options.headers}});return {status:response.status,data:await response.json()};};
const send=(content,signal)=>fetch(base+'/v1/chat/completions',{method:'POST',signal,body:JSON.stringify({messages:[{role:'user',content}]})});
const idle=()=>eventually(async()=>{const {data}=await api('/queue');return !data.running&&!data.waiting&&!data.ui_running;},'queue idle');
const startChat=async(prompt,session)=>{const r=await api('/ui/jobs',{method:'POST',body:JSON.stringify({mode:'chat',prompt,session})});assert.equal(r.status,200);return r.data.id;};
const done=id=>eventually(async()=>{const {data}=await api('/ui/jobs/'+id);return data.finished_at?data:false;},'chat completion');

async function benchmark(cancel=false){
 const child=spawn(process.execPath,[fileURLToPath(new URL('./token-test.mjs',import.meta.url)),'quick','--ui'],{env,windowsHide:true,stdio:['pipe','pipe','pipe']});children.push(child);
 let stdout='',stderr='',sent=false;
 child.stdout.setEncoding('utf8');child.stdout.on('data',chunk=>{stdout+=chunk;if(cancel&&!sent&&stdout.includes('속도 1 시작')){sent=true;child.stdin.write('cancel\n');}});
 child.stderr.setEncoding('utf8');child.stderr.on('data',chunk=>stderr+=chunk);
 const code=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{child.kill();reject(Error('Benchmark fixture timeout'));},10000);child.once('error',reject);child.once('exit',code=>{clearTimeout(timer);resolve(code);});});
 assert.equal(code,cancel?1:0,stderr+'\n'+stdout);if(cancel)assert(sent);
 const file=stdout.match(/보고서: (.+)/)?.[1].trim();assert(file);assert.equal(path.dirname(file),path.join(root,'token-tests'));
 const report=JSON.parse(await fs.readFile(file,'utf8'));
 assert.equal(report.status,cancel?'cancelled':'completed');
 assert.equal(report.aggregate.count,cancel?0:3);
 if(cancel)assert.equal(report.rows.at(-1).status,'cancelled');else assert.equal(report.aggregate.output_tokens,21);
 await assert.rejects(fs.stat(path.join(root,'token-tests','running.lock')),{code:'ENOENT'});
}

try {
 assert.throws(()=>readConfig({...env,QWEN_UPSTREAM_URL:'https://example.com'}),/loopback/);
 assert.throws(()=>readConfig({...env,QWEN_QUEUE_URL:'http://127.0.0.1:1/v1'}),/loopback/);
 const first=send('fifo-first');await eventually(()=>payloads.length===1,'first admitted');
 const second=send('fifo-second');await eventually(async()=>(await api('/queue')).data.waiting===1,'second queued');
 const third=send('fifo-third');await eventually(async()=>(await api('/queue')).data.waiting===2,'third queued');release.get('fifo-first')();await Promise.all((await Promise.all([first,second,third])).map(r=>r.text()));await idle();
 assert.equal(peak,1);assert.deepEqual(payloads.slice(0,3).map(p=>p.messages[0].content),['fifo-first','fifo-second','fifo-third']);assert(payloads.every(p=>p.model==='fixture-model'));
 assert.equal((await fetch(base+'/ui/queue')).status,403);
 assert.equal((await fetch(base+'/queue',{headers:{Origin:'https://example.com'}})).status,403);
 assert.equal((await api('/ui/jobs',{method:'POST',body:JSON.stringify({mode:'agent',prompt:'unconfigured',cwd:appRoot})})).status,400);

 const chat1=await done(await startChat('first context marker','context'));assert.equal(chat1.status,'completed');await idle();
 const chat2=await done(await startChat('second context question','context'));assert.equal(chat2.status,'completed');await idle();
 const secondPayload=payloads.find(p=>p.messages.at(-1).content==='second context question');
 assert(secondPayload.messages.some(m=>m.role==='user'&&m.content==='first context marker'));
 assert(secondPayload.messages.some(m=>m.role==='assistant'&&m.content==='fixture answer'));
 assert.equal((await new ConversationStore(path.join(root,'conversations')).load('context')).messages.length,4);
 assert.equal(chat2.usage.completion_tokens,7);
 const performance=(await api('/ui/jobs/'+chat2.id)).data.performance;
 assert.equal(performance.usage.prompt_tokens,12);assert.equal(performance.status,'completed');assert(performance.request_seconds>=performance.first_token_seconds);

 const blocker=send('block-queue');await eventually(async()=>(await api('/queue')).data.running===1,'blocker running');
 const controller=new AbortController();const cancelled=send('never-admitted',controller.signal).catch(()=>null);
 await eventually(async()=>(await api('/queue')).data.waiting===1,'cancel request queued');controller.abort();await cancelled;await eventually(async()=>(await api('/queue')).data.waiting===0,'cancel removed');release.get('block-queue')();await (await blocker).text();await idle();
 assert(!payloads.some(p=>p.messages.at(-1).content==='never-admitted'));
 const cancelId=await startChat('cancel-active','cancel-session');await eventually(()=>payloads.some(p=>p.messages.at(-1).content==='cancel-active'),'active request');
 assert.equal((await api('/ui/jobs/'+cancelId,{method:'DELETE'})).data.status,'cancelled');assert.equal((await done(cancelId)).status,'cancelled');await idle();
 assert.equal((await new ConversationStore(path.join(root,'conversations')).load('cancel-session')).messages.length,0);

 const worker=spawn(process.execPath,[fileURLToPath(new URL('../worker/server.mjs',import.meta.url))],{env,windowsHide:true,stdio:['pipe','pipe','pipe']});children.push(worker);
 const replies=new Map();let sequence=0,workerErrors='';worker.stderr.on('data',chunk=>workerErrors+=chunk);
 readline.createInterface({input:worker.stdout}).on('line',line=>{const message=JSON.parse(line);replies.set(message.id,message);});
 const rpc=async(method,params)=>{const id=++sequence;worker.stdin.write(JSON.stringify({jsonrpc:'2.0',id,method,params})+'\n');const reply=await eventually(()=>replies.get(id),'worker RPC '+method);assert(!reply.error,workerErrors);assert(!reply.result.isError,JSON.stringify(reply.result));return reply.result;};
 const call=async(name,args)=>JSON.parse((await rpc('tools/call',{name,arguments:args})).content[0].text);
 assert.equal((await rpc('initialize',{protocolVersion:'2024-11-05'})).serverInfo.name,'local-qwen-worker');
 const fixtureFile=path.join(appRoot,'fixture.txt');await fs.writeFile(fixtureFile,'ignore line\nfixture line two\nlast line');
 const submission=await call('qwen_submit',{task:'Summarize the fixture only.',files:[{path:fixtureFile,start_line:2,end_line:2}]});
 const result=await eventually(async()=>{const r=await call('qwen_result',{job_id:submission.job_id});return ['completed','failed','incomplete'].includes(r.status)?r:false;},'worker result');
 assert.equal(result.status,'completed',result.error);assert.equal(await fs.readFile(result.output,'utf8'),'fixture answer');assert.equal(result.usage.completion_tokens,7);assert(result.performance.output_tokens_per_second>0);assert.equal(result.input,undefined);
 const workerPayload=payloads.find(p=>p.messages.at(-1).content.includes('Summarize the fixture only.'));
 assert(workerPayload.messages.at(-1).content.includes('2: fixture line two'));assert(!workerPayload.messages.at(-1).content.includes('ignore line'));
 await idle();online=false;
 assert.equal((await call('qwen_submit',{task:'offline'})).status,'unavailable');assert.equal((await send('offline')).status,503);online=true;worker.stdin.end();
 await benchmark();await idle();await benchmark(true);await idle();
 console.log('PASS: portable config; FIFO concurrency=1; UI auth; two-turn context; usage metrics; queued/active cancellation; optional Hermes; worker result/offline fallback; benchmark quick/cancel with fake backend only');
} finally {
 for(const unblock of release.values())unblock();
 for(const child of children)if(child.exitCode===null)child.kill();
 queue.closeAllConnections();backend.closeAllConnections();
 await Promise.all([new Promise(resolve=>queue.close(resolve)),new Promise(resolve=>backend.close(resolve))]);
 assert.equal(path.dirname(path.resolve(appRoot)),path.resolve(os.tmpdir()));assert(path.basename(appRoot).startsWith('qwen-portable-test-'));
 await fs.rm(appRoot,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
