import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import {createGateway} from './gateway.mjs';

const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-context-boundary-'));
let generationCalls=0,contextSize=32768;
const requests=[];
const fake=http.createServer(async(req,res)=>{
 res.setHeader('content-type','application/json');
 if(req.url==='/health')return res.end(JSON.stringify({service:'strata',loaded:true,model:'fixture-context',max_context:contextSize}));
 if(req.url==='/v1/models')return res.end(JSON.stringify({data:[{id:'fixture-context',meta:{n_ctx:contextSize}}]}));
 let raw='';for await(const data of req)raw+=data;const payload=JSON.parse(raw);
 const tokens=Number(payload.messages.at(-1).content.match(/^count=(\d+)/)[1]);
 if(req.url==='/tokenize')return res.end(JSON.stringify({count:tokens}));
 assert.equal(req.url,'/v1/chat/completions');generationCalls++;requests.push(payload);
 res.setHeader('content-type','text/event-stream');
 res.end('data: '+JSON.stringify({choices:[{delta:{content:'within context'},finish_reason:'stop'}],usage:{prompt_tokens:tokens,completion_tokens:1}})+'\n\ndata: [DONE]\n\n');
});
await new Promise(r=>fake.listen(0,'127.0.0.1',r));
const upstream='http://127.0.0.1:'+fake.address().port;
const gateways=[];
delete process.env.QWEN_BACKEND_FILE;delete process.env.QWEN_CONTEXT_TOKENS;
process.env.QWEN_BACKEND='strata';process.env.QWEN_MODEL_ID='fixture-context';
try {
 for(const capacity of [32768,262144]){
  contextSize=capacity;const inputLimit=capacity-4096;
  const dataRoot=path.join(root,String(capacity)),gateway=await createGateway({port:0,upstream,root:dataRoot});gateways.push(gateway);
  const base='http://127.0.0.1:'+gateway.address().port;
  const caps=await(await fetch(base+'/capabilities')).json();assert.equal(caps.context_tokens,capacity);assert.equal(caps.chat_input_budget,inputLimit);
  const headers={'x-ui-token':(await fs.readFile(path.join(dataRoot,'ui-token.txt'),'utf8')).trim()};
  const submit=prompt=>fetch(base+'/ui/jobs',{method:'POST',headers,body:JSON.stringify({mode:'chat',session:String(generationCalls),prompt})});
  async function finish(response){assert.equal(response.status,200);const {id}=await response.json();for(let i=0;i<250;i++){const job=await(await fetch(base+'/ui/jobs/'+id,{headers})).json();if(job.finished_at)return job;await new Promise(r=>setTimeout(r,20));}throw Error('Context fixture timeout');}
  // Character length is independent of the verified server's actual token count.
  const accepted=await finish(await submit('count='+inputLimit+'\n'+'x'.repeat(13000)));
  assert.equal(accepted.status,'completed',accepted.error);assert.equal(accepted.context_tokens,inputLimit);
  assert.equal(requests.at(-1).max_tokens,4096);assert.equal(inputLimit+requests.at(-1).max_tokens,capacity);
  const before=generationCalls;
  const rejected=await finish(await submit('count='+(inputLimit+1)+'\n'+'x'.repeat(13000)));
  assert.equal(rejected.status,'failed');assert.match(rejected.error,/한도/);assert.equal(generationCalls,before,'out-of-budget input must never reach inference');
  const oversized=await submit('count=1\n'+'x'.repeat(4_000_000));assert.equal(oversized.status,400);
 }
 console.log('PASS: Strata reported 32K/256K context boundaries; 4,096 output reservation; >12,000-character valid prompts; one-token overflow rejected before inference; 4M character safety bound');
}finally{
 for(const gateway of gateways)gateway.closeAllConnections();fake.closeAllConnections();
 await Promise.all([...gateways,fake].map(server=>new Promise(r=>server.close(r))));
 assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));assert(path.basename(root).startsWith('qwen-context-boundary-'));
 await fs.rm(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
}
