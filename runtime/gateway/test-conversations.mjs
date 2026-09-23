import {ConversationStore} from './conversation-store.mjs';
import fs from 'node:fs/promises';import os from 'node:os';import path from 'node:path';import assert from 'node:assert/strict';
const root=await fs.mkdtemp(path.join(os.tmpdir(),'qwen-memory-'));
const count=async messages=>messages.reduce((n,m)=>n+m.content.length,0);
try{
 const store=new ConversationStore(root,{budget:1600}),key='../../outside';
 assert.equal(path.dirname(store.file(key)),root);
 for(let i=0;i<8;i++){const p=await store.prepare(key,'question'+i,count,async()=>{throw Error('Unexpected summary');});await store.commit(key,p,'question'+i,'answer'+i);}
 const restored=await new ConversationStore(root).load(key);assert.equal(restored.messages.length,16);assert.equal(restored.archive.length,16);
 const big={messages:Array.from({length:10},(_,i)=>({role:i%2?'assistant':'user',content:'X'.repeat(250)+i})),summary:'earlier fact',archive:[]};await store.save('big',big);
 let summaries=0;const p=await store.prepare('big','new',count,async(summary,older)=>{summaries++;assert.equal(summary,'earlier fact');assert.equal(older.length,6);return 'preserved decisions';});
 assert.equal(summaries,1);assert(p.tokens<=1600);assert.equal(p.state.messages.length,4);assert.equal(p.state.messages[0].content,big.messages[6].content);
 await store.commit('big',p,'new','new answer');assert.equal((await store.load('big')).summary,'preserved decisions');
 await store.save('failure',big);const original=await fs.readFile(store.file('failure'),'utf8');
 await assert.rejects(()=>store.prepare('failure','new',count,async()=>{throw Error('summary failed');}),/summary failed/);assert.equal(await fs.readFile(store.file('failure'),'utf8'),original);
 await assert.rejects(()=>store.prepare('new','Z'.repeat(1700),count,async()=>''),/한도/);
 await fs.writeFile(store.file('corrupt'),'{broken');await assert.rejects(()=>store.load('corrupt'));assert.equal(await fs.readFile(store.file('corrupt'),'utf8'),'{broken');
 console.log('PASS: restart persistence; session isolation; token-budget compaction; recent turns preserved; failed summary rollback; oversized prompt rejected; corrupt state preserved');
}finally{for(const name of await fs.readdir(root))await fs.unlink(path.join(root,name));await fs.rmdir(root);}
