import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import {createGateway} from './gateway.mjs';
import {logMessages} from './media-policy.mjs';
delete process.env.QWEN_BACKEND_FILE;process.env.QWEN_BACKEND='vllm';
const image={type:'image_url',image_url:{url:'data:image/png;base64,'+'A'.repeat(2_100_000)}};
let ready=false,received;
const backend=http.createServer(async(req,res)=>{
 if(req.url==='/health')return res.end('OK');
 let raw='';for await(const b of req)raw+=b;
 received=JSON.parse(raw);res.setHeader('content-type','application/json');
 res.end(JSON.stringify({choices:[{message:{content:'image received'},finish_reason:'stop'}]}));
});
await new Promise(r=>backend.listen(0,'127.0.0.1',r));
const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-media-'));
const server=await createGateway({port:0,root,upstream:'http://127.0.0.1:'+backend.address().port,visionReady:async()=>ready});
const base='http://127.0.0.1:'+server.address().port;
const send=content=>fetch(base+'/v1/chat/completions',{method:'POST',body:JSON.stringify({messages:[{role:'user',content}]})});
try {
 assert.equal((await send([image])).status,400);assert.equal(received,undefined);
 ready=true;
 assert.equal((await send([image,image])).status,400);
 assert.equal((await send([{type:'video_url',video_url:{url:'sample'}}])).status,400);
 const r=await send([{type:'text',text:'read photo'},image]);assert.equal(r.status,200);await r.text();
 assert.equal(received.messages[0].content[1].image_url.url,image.image_url.url);
 assert(!JSON.stringify(logMessages(received.messages)).includes('base64'));
 assert((await (await fetch(base+'/capabilities')).json()).vision_ready);
 while((await (await fetch(base+'/queue')).json()).running)await new Promise(r=>setTimeout(r,20));
 const logs=await fs.readdir(path.join(root,'requests'));
 const request=await fs.readFile(path.join(root,'requests',logs.find(x=>x.endsWith('.request.txt'))),'utf8');
 assert(request.includes('read photo'));assert(!request.includes('base64'));
 console.log('PASS: vision-off rejection; image-count/video limits; >2MB image forwarding intact; binary log redaction');
} finally {
 server.closeAllConnections();backend.closeAllConnections();
 await Promise.all([new Promise(r=>server.close(r)),new Promise(r=>backend.close(r))]);
 // Delete only the exact mkdtemp-owned fixture tree.
 assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));assert(path.basename(root).startsWith('qwen-media-'));
 await fs.rm(root,{recursive:true});
}
