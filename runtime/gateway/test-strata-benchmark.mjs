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

const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-strata-benchmark-'));
const model='fixture-sc117-iq2',capacity=262144,generations=[];
let benchmark,status=200;
const fake=http.createServer(async(req,res)=>{
 res.setHeader('content-type','application/json');
 if(req.url==='/health')return res.end(JSON.stringify({service:'strata',loaded:true,model,max_context:capacity}));
 if(req.url==='/v1/models')return res.end(JSON.stringify({data:[{id:model,meta:{n_ctx:capacity}}]}));
 let text='';for await(const chunk of req)text+=chunk;const body=JSON.parse(text);
 const prompt=body.messages.map(m=>m.content).join(''),tokens=Math.ceil(prompt.length/5);
 if(req.url==='/tokenize'){res.writeHead(status);return res.end(JSON.stringify({count:tokens}));}
 assert.equal(req.url,'/v1/chat/completions');generations.push(body);
 assert.equal(body.model,model);assert(tokens+body.max_tokens<=capacity,'actual Strata context includes output');
 const markers=[...prompt.matchAll(/SECRET_KEY_[0-2]=([^\s]+)/g)].map(m=>m[1]);
 res.setHeader('content-type','text/event-stream');
 res.end('data: '+JSON.stringify({choices:[{delta:{content:markers.length?markers.join(' '):'fixture answer'},finish_reason:'stop'}]})+'\n\ndata: '+JSON.stringify({choices:[],usage:{prompt_tokens:tokens,completion_tokens:7}})+'\n\ndata: [DONE]\n\n');
});
await new Promise(resolve=>fake.listen(0,'127.0.0.1',resolve));
const upstream='http://127.0.0.1:'+fake.address().port;
const backendFile=path.join(root,'backend.txt');await fs.writeFile(backendFile,'strata');
process.env.QWEN_BACKEND='strata';process.env.QWEN_BACKEND_FILE=backendFile;process.env.QWEN_MODEL_ID=model;process.env.QWEN_CONTEXT_TOKENS=String(capacity);
const gateway=await createGateway({port:0,upstream,root}),base='http://127.0.0.1:'+gateway.address().port;
try {
 const messages=[{role:'user',content:'hello'}];
 status=503;await assert.rejects(countTokens({upstream,backend:'strata',model,messages}),/503/);status=200;
 for(const invalid of ['vllm','ninfer','invalid','strata'+' '.repeat(65)]){
  await fs.writeFile(backendFile,invalid);await assert.rejects(readBackend({backendFile,backend:'strata'}),/only strata/);
  assert.equal((await fetch(base+'/tokenize',{method:'POST',body:JSON.stringify({messages})})).status,400);
 }
 await fs.unlink(backendFile);assert.equal(await readBackend({backendFile,backend:'strata'}),'strata');
 await fs.mkdir(backendFile);await assert.rejects(readBackend({backendFile,backend:'strata'}));await fs.rmdir(backendFile);
 await fs.writeFile(backendFile,'strata');
 // Exercise the actual benchmark runner and token-search algorithm without inference/GPU.
 benchmark=spawn(process.execPath,[fileURLToPath(new URL('./token-test.mjs',import.meta.url)),'224k'],{windowsHide:true,env:{...process.env,QWEN_GATEWAY_DATA_DIR:root,QWEN_UPSTREAM_URL:upstream,QWEN_QUEUE_URL:base},stdio:['ignore','pipe','pipe']});
 let output='',error='';benchmark.stdout.on('data',s=>output+=s);benchmark.stderr.on('data',s=>error+=s);
 const code=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{benchmark.kill();reject(Error('224K fixture timeout'));},15000);benchmark.once('error',reject);benchmark.once('exit',code=>{clearTimeout(timer);resolve(code);});});
 assert.equal(code,0,error+'\n'+output);
 const reportFile=output.match(/보고서: (.+)/)?.[1].trim();assert(reportFile);assert.equal(path.dirname(reportFile),path.join(root,'token-tests'));
 const report=JSON.parse(await fs.readFile(reportFile,'utf8'));
 assert.equal(report.backend,'strata');assert.equal(report.model,model);assert.equal(report.context_tokens,capacity);assert.equal(report.status,'completed');assert.equal(report.rows.at(-1).recall_pass,true);assert.equal(report.aggregate.count,1);
 assert(report.rows.at(-1).input_tokens>228000);assert(report.rows.at(-1).input_tokens+256<=229120,'224K profile retains output and template reservation');
 assert.equal(generations.length,2,'warmup and one measured generation only');assert.equal(generations.at(-1).max_tokens,256);
 await assert.rejects(fs.stat(path.join(root,'token-tests','running.lock')),{code:'ENOENT'});
 console.log('PASS: Strata-only backend-file rejection/size/missing/unreadable cases; count HTTP errors; 224K benchmark via FIFO with actual identity/context, recall, usage, output/template reservation and lock cleanup (fake server only)');
}finally{
 if(benchmark&&benchmark.exitCode===null)benchmark.kill();
 gateway.closeAllConnections();fake.closeAllConnections();
 await Promise.all([gateway,fake].map(server=>new Promise(resolve=>server.close(resolve))));
 assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));assert(path.basename(root).startsWith('qwen-strata-benchmark-'));
 await fs.rm(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
