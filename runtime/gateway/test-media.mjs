import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import {createGateway} from './gateway.mjs';
import {logMessages} from './media-policy.mjs';
delete process.env.QWEN_BACKEND_FILE;delete process.env.QWEN_CONTEXT_TOKENS;process.env.QWEN_BACKEND='strata';process.env.QWEN_MODEL_ID='fixture-media';
const image={type:'image_url',image_url:{url:'data:image/png;base64,'+'A'.repeat(2_100_000)}};
let received=0;
const backend=http.createServer(async(req,res)=>{
 res.setHeader('content-type','application/json');
 if(req.url==='/health')return res.end(JSON.stringify({service:'strata',loaded:true,model:'fixture-media',max_context:32768,images:true}));
 if(req.url==='/v1/models')return res.end(JSON.stringify({data:[{id:'fixture-media',meta:{n_ctx:32768},architecture:{input_modalities:['text','image']}}]}));
 received++;for await(const b of req){}
 res.writeHead(500);res.end('{}');
});
await new Promise(r=>backend.listen(0,'127.0.0.1',r));
const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-media-'));
const server=await createGateway({port:0,root,upstream:'http://127.0.0.1:'+backend.address().port});
const base='http://127.0.0.1:'+server.address().port;
const send=content=>fetch(base+'/v1/chat/completions',{method:'POST',body:JSON.stringify({messages:[{role:'user',content}]})});
try {
 for(const content of [[image],[image,image],[{type:'video_url',video_url:{url:'sample'}}],[{type:'text',text:'read photo'},image]]){
  const response=await send(content);assert.equal(response.status,400);await response.text();
 }
 assert.equal(received,0,'media must not reach counting or generation');
 assert.equal((await (await fetch(base+'/capabilities')).json()).vision_ready,false);
 const redacted=JSON.stringify(logMessages([{role:'user',content:[{type:'text',text:'read photo'},image]}]));
 assert(redacted.includes('read photo'));assert(!redacted.includes('base64'));assert(!redacted.includes('A'.repeat(100)));
 assert.deepEqual(await fs.readdir(path.join(root,'requests')),[],'rejected media must not leave request data');
 console.log('PASS: Strata text-only even when upstream advertises images; single/multiple/>2MB image and video rejection before counting/generation; binary log redaction; no rejected request data');
} finally {
 server.closeAllConnections();backend.closeAllConnections();
 await Promise.all([new Promise(r=>server.close(r)),new Promise(r=>backend.close(r))]);
 assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));assert(path.basename(root).startsWith('qwen-media-'));
 await fs.rm(root,{recursive:true});
}
