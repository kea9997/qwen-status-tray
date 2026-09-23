import assert from 'node:assert/strict';
import {readStream} from './stream-response.mjs';
const text='data: '+JSON.stringify({choices:[{delta:{content:'안녕'}}]})+'\r\n\r\ndata: '+JSON.stringify({choices:[{delta:{content:'하세요'},finish_reason:'stop'}]})+'\n\ndata: '+JSON.stringify({choices:[],usage:{prompt_tokens:4,completion_tokens:3}})+'\n\ndata: [DONE]\n\n';
async function* chunks(value){const bytes=Buffer.from(value);for(let i=0;i<bytes.length;i+=2)yield bytes.subarray(i,i+2);}
let preview='';const result=await readStream(chunks(text),async s=>{preview=s;});
assert.equal(result.choices[0].message.content,'안녕하세요');assert.equal(preview,'안녕하세요');assert.equal(result.usage.completion_tokens,3);
await assert.rejects(readStream(chunks('data: {"choices":[{"delta":{"content":"partial"}}]}\n'),async()=>{}),/before completion/);
console.log('PASS: fragmented UTF-8 SSE, usage, final content, incomplete-stream rejection');
