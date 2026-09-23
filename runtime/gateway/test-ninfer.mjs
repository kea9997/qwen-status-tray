import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import {spawn} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {createGateway} from './gateway.mjs';
import {countTokens} from './backend-adapter.mjs';
import {readBackend} from './runtime-config.mjs';

const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-ninfer-test-'));
const counts=[],generations=[],metrics='llamacpp:tokens_predicted_total 7\n';
let malformed=false,status=200,benchmark;
const toolEvents=[
 {choices:[{delta:{tool_calls:[{index:0,id:'call-fixture',type:'function',function:{name:'read_file',arguments:'{"path":'}}]},finish_reason:null}]},
 {choices:[{delta:{tool_calls:[{index:0,function:{arguments:'"fixture.txt"}'}}]},finish_reason:null}]},
 {choices:[{delta:{},finish_reason:'tool_calls'}]},
 {choices:[],usage:{prompt_tokens:11,completion_tokens:7,total_tokens:18}},
];
const toolWire=toolEvents.map(e=>'data: '+JSON.stringify(e)+'\n\n').join('')+'data: [DONE]\n\n';
const fake=http.createServer(async(req,res)=>{
 if(req.url==='/health')return res.end();
 if(req.url==='/metrics')return res.end(metrics);
 let text='';for await(const chunk of req)text+=chunk;const body=JSON.parse(text);
 if(req.url==='/v1/messages/count_tokens'){
  counts.push(body);assert.equal(body.model,'fixture-ninfer');assert(body.messages.every(m=>['user','assistant'].includes(m.role)));
  res.writeHead(status,{'content-type':'application/json'});
  return res.end(JSON.stringify({input_tokens:malformed?null:Math.ceil((String(body.system||'')+body.messages.map(m=>m.content).join('')).length/5)}));
 }
 // This route is selected only by the explicit live switch to vLLM below.
 if(req.url==='/tokenize')return res.end(JSON.stringify({count:123}));
 assert.equal(req.url,'/v1/chat/completions');generations.push(body);
 const promptTokenCount=Math.ceil(body.messages.map(m=>m.content).join('').length/5);
 if(promptTokenCount+body.max_tokens>229376){res.writeHead(400);return res.end('context overflow');}
 res.setHeader('content-type','text/event-stream');
 if(body.tools){res.write(toolWire.slice(0,53));return setTimeout(()=>res.end(toolWire.slice(53)),5);}
 const prompt=body.messages.at(-1).content;
 const markers=[...prompt.matchAll(/SECRET_KEY_[0-2]=([^\s]+)/g)].map(m=>m[1]);
 const content=markers.length?markers.join(' '):'fixture ninfer answer';
 res.end('data: '+JSON.stringify({choices:[{delta:{content},finish_reason:'stop'}]})+'\n\ndata: '+JSON.stringify({choices:[],usage:{prompt_tokens:Math.ceil(prompt.length/5),completion_tokens:7}})+'\n\ndata: [DONE]\n\n');
});
await new Promise(resolve=>fake.listen(0,'127.0.0.1',resolve));
const upstream='http://127.0.0.1:'+fake.address().port;
const backendFile=path.join(root,'backend.txt');await fs.writeFile(backendFile,'ninfer');
process.env.QWEN_BACKEND='vllm';process.env.QWEN_BACKEND_FILE=backendFile;process.env.QWEN_MODEL_ID='fixture-ninfer';
const gateway=await createGateway({port:0,upstream,root,visionReady:async()=>true});
const base='http://127.0.0.1:'+gateway.address().port;
const headers={'x-ui-token':(await fs.readFile(path.join(root,'ui-token.txt'),'utf8')).trim()};
const pause=()=>new Promise(r=>setTimeout(r,20));
async function eventually(fn){for(let i=0;i<250;i++){const result=await fn();if(result)return result;await pause();}throw Error('Fake NInfer fixture timeout');}
try {
 const messages=[{role:'system',content:'system one'},{role:'user',content:'hello'},{role:'assistant',content:'prior'},{role:'system',content:'system two'}];
 const counted=await countTokens({upstream,backend:'ninfer',model:'fixture-ninfer',messages});
 assert(Number.isInteger(counted));assert.equal(counts[0].system,'system one\nsystem two');assert.deepEqual(counts[0].messages,messages.filter(m=>m.role!=='system'));
 assert.equal(counts[0].add_generation_prompt,undefined);assert.equal(counts[0].chat_template_kwargs,undefined);
 malformed=true;await assert.rejects(countTokens({upstream,backend:'ninfer',model:'fixture-ninfer',messages}),/Invalid token count/);malformed=false;
 status=503;await assert.rejects(countTokens({upstream,backend:'ninfer',model:'fixture-ninfer',messages}),/503/);status=200;
 await assert.rejects(countTokens({upstream,backend:'ninfer',model:'fixture-ninfer',messages:[{role:'tool',content:'unsupported'}]}),/text system/);
 const normalized=await(await fetch(base+'/tokenize',{method:'POST',body:JSON.stringify({messages})})).json();assert.equal(normalized.count,counted);
 assert.equal((await(await fetch(base+'/capabilities')).json()).vision_ready,false);
 assert.equal(await(await fetch(base+'/metrics')).text(),metrics);
 await fs.writeFile(backendFile,'vllm');
 assert.equal((await(await fetch(base+'/capabilities')).json()).vision_ready,true);
 assert.equal((await(await fetch(base+'/tokenize',{method:'POST',body:JSON.stringify({messages})})).json()).count,123);
 await fs.writeFile(backendFile,'invalid');assert.equal((await fetch(base+'/tokenize',{method:'POST',body:JSON.stringify({messages})})).status,400);
 await fs.writeFile(backendFile,'ninfer'+' '.repeat(65));await assert.rejects(readBackend({backendFile,backend:'vllm'}),/64 bytes/);
 await fs.unlink(backendFile);assert.equal(await readBackend({backendFile,backend:'vllm'}),'vllm');
 await fs.writeFile(backendFile,'ninfer');

 const tools=[{type:'function',function:{name:'read_file',parameters:{type:'object',properties:{path:{type:'string'}}}}}];
 const response=await fetch(base+'/v1/chat/completions',{method:'POST',body:JSON.stringify({messages:[{role:'user',content:'Read the fixture'}],tools,stream:true})});
 assert.equal(response.status,200);assert.equal(await response.text(),toolWire);assert.deepEqual(generations[0].tools,tools);
 const requestId=response.headers.get('x-qwen-request-id');
 const record=await eventually(async()=>{const data=JSON.parse(await fs.readFile(path.join(root,'requests',requestId+'.json'),'utf8'));return data.finished_at?data:null;});
 assert.equal(record.status,'completed');assert.equal(record.usage.completion_tokens,7);

 for(const prompt of ['first ninfer context','second ninfer context']){
  const started=await fetch(base+'/ui/jobs',{method:'POST',headers,body:JSON.stringify({mode:'chat',session:'ninfer-context',prompt})});assert.equal(started.status,200);
  const {id}=await started.json();const job=await eventually(async()=>{const d=await(await fetch(base+'/ui/jobs/'+id,{headers})).json();return d.finished_at?d:null;});assert.equal(job.status,'completed',job.error);
 }
 assert(counts.some(c=>c.messages.some(m=>m.content==='first ninfer context')&&c.messages.some(m=>m.content==='second ninfer context')));
 await eventually(async()=>{const q=await(await fetch(base+'/queue')).json();return !q.running&&!q.waiting;});

 // Enforce a real 224 Ki-token total limit, including output, on the benchmark fixture.
 benchmark=spawn(process.execPath,[fileURLToPath(new URL('./token-test.mjs',import.meta.url)),'224k'],{windowsHide:true,env:{...process.env,QWEN_GATEWAY_DATA_DIR:root,QWEN_UPSTREAM_URL:upstream,QWEN_QUEUE_URL:base},stdio:['ignore','pipe','pipe']});
 let output='',error='';benchmark.stdout.on('data',s=>output+=s);benchmark.stderr.on('data',s=>error+=s);
 const code=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{benchmark.kill();reject(Error('224K fixture timeout'));},15000);benchmark.once('error',reject);benchmark.once('exit',code=>{clearTimeout(timer);resolve(code);});});
 assert.equal(code,0,error+'\n'+output);
 const reportFile=output.match(/보고서: (.+)/)?.[1].trim();assert.equal(path.dirname(reportFile),path.join(root,'token-tests'));
 const report=JSON.parse(await fs.readFile(reportFile,'utf8'));assert.equal(report.status,'completed');assert.equal(report.rows.at(-1).recall_pass,true);assert.equal(report.aggregate.count,1);
 assert(report.rows.at(-1).input_tokens>228000);assert(report.rows.at(-1).input_tokens+256<=229120,'reserve 256 output plus 256 template headroom below 229,376 total');
 console.log('PASS: NInfer count_tokens conversion; live backend-file switch/validation; systems separated; invalid/error rejection; normalized /tokenize; text-only capability; exact streaming tool-call forwarding; usage; two-turn chat; 224K benchmark with output/headroom reservation (fake backend only)');
}finally{
 if(benchmark&&benchmark.exitCode===null)benchmark.kill();
 gateway.closeAllConnections();fake.closeAllConnections();
 await Promise.all([new Promise(resolve=>gateway.close(resolve)),new Promise(resolve=>fake.close(resolve))]);
 assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));assert(path.basename(root).startsWith('qwen-ninfer-test-'));
 await fs.rm(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
