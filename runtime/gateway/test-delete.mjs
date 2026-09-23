import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {createGateway} from './gateway.mjs';
import {ConversationStore} from './conversation-store.mjs';
import {AgentSessions} from './agent-sessions.mjs';
delete process.env.QWEN_BACKEND_FILE;process.env.QWEN_BACKEND='vllm';
let online=true;
const backend=http.createServer(async(req,res)=>{
 if(req.url==='/health'){res.writeHead(online?200:503);return res.end();}
 for await(const b of req){}
 res.setHeader('content-type','application/json');
 if(req.url==='/tokenize')return res.end(JSON.stringify({count:10}));
 await new Promise(r=>setTimeout(r,200));
 res.setHeader('content-type','text/event-stream');
 res.end('data: '+JSON.stringify({choices:[{delta:{content:'answer'},finish_reason:'stop'}]})+'\n\ndata: [DONE]\n\n');
});
await new Promise(r=>backend.listen(0,'127.0.0.1',r));
const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-delete-api-'));
const store=new ConversationStore(path.join(root,'conversations'));
const original={messages:[{role:'user',content:'keep'}],summary:'summary',archive:[]};
await store.save('removed',original);await store.save('kept',original);
const server=await createGateway({port:0,root,upstream:'http://127.0.0.1:'+backend.address().port});
const base='http://127.0.0.1:'+server.address().port;
const headers={'x-ui-token':(await fs.readFile(path.join(root,'ui-token.txt'),'utf8')).trim()};
const remove=key=>fetch(base+'/ui/conversations/'+key,{method:'DELETE',headers});
try{
 const agents=new AgentSessions(path.join(root,'agent-sessions'));
 await agents.save('removed',root,'20260915_123456_abcd');await agents.save('kept',root,'20260915_123456_efab');
 const removeAgent=key=>fetch(base+'/ui/agent-conversations/'+key,{method:'DELETE',headers});
 assert.equal((await fetch(base+'/ui/agent-conversations/removed',{method:'DELETE'})).status,403);
 online=false;
 assert.equal((await removeAgent('removed')).status,200);
 assert.equal(await agents.load('removed',root),null);
 assert.equal(await agents.load('kept',root),'20260915_123456_efab');
 assert.equal((await store.load('removed')).summary,'summary');
 assert.equal((await removeAgent('removed')).status,200);
 assert.equal((await fetch(base+'/ui/conversations/removed',{method:'DELETE'})).status,403);
 online=false;
 assert.equal((await remove('removed')).status,200);
 assert.deepEqual((await store.load('removed')).messages,[]);
 assert.equal((await store.load('kept')).summary,'summary');
 assert.equal((await remove('removed')).status,200);
 online=true;
 const start=await fetch(base+'/ui/jobs',{method:'POST',headers,body:JSON.stringify({mode:'chat',prompt:'fixture',session:'active'})});
 const {id}=await start.json();
 assert.equal((await remove('active')).status,409);
 await fetch(base+'/ui/jobs/'+id,{method:'DELETE',headers});
 let done=false;
 for(let i=0;i<100;i++){
  const job=await(await fetch(base+'/ui/jobs/'+id,{headers})).json();
  if(job.finished_at){done=true;break;}
  await new Promise(r=>setTimeout(r,20));
 }
 assert(done);assert.equal((await remove('active')).status,200);
 assert.equal((await fetch(base+'/ui/jobs/'+id,{headers})).status,404);
 assert.deepEqual((await store.load('active')).messages,[]);
 console.log('PASS: authenticated deletion; model-off deletion; idempotency; other session preserved; active request protected; cancelled request deletion');
}finally{
 server.closeAllConnections();backend.closeAllConnections();
 await Promise.all([new Promise(r=>server.close(r)),new Promise(r=>backend.close(r))]);
 assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));
 assert(path.basename(root).startsWith('qwen-delete-api-'));
 await fs.rm(root,{recursive:true});
}
