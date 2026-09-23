import fs from 'node:fs/promises';import path from 'node:path';import {createHash,randomUUID} from 'node:crypto';
export class AgentSessions{
 constructor(root){this.root=root;}
 file(key){return path.join(this.root,createHash('sha256').update(String(key)).digest('hex')+'.json');}
 async delete(key){try{await fs.unlink(this.file(key));}catch(e){if(e.code!=='ENOENT')throw e;}}
 async load(key,cwd){
  try{const d=JSON.parse(await fs.readFile(this.file(key),'utf8'));if(!/^\d{8}_\d{6}_[a-f0-9]+$/.test(d.session))throw Error('Invalid Hermes session');if(path.resolve(d.cwd).toLowerCase()!==path.resolve(cwd).toLowerCase())throw Error('이어가는 작업의 폴더는 바꿀 수 없습니다. 새 작업을 만들어 주세요.');return d.session;}
  catch(e){if(e.code==='ENOENT')return null;throw e;}
 }
 async save(key,cwd,session){if(!/^\d{8}_\d{6}_[a-f0-9]+$/.test(session))throw Error('Invalid Hermes session');await fs.mkdir(this.root,{recursive:true});const file=this.file(key),tmp=file+'.'+randomUUID()+'.tmp';try{await fs.writeFile(tmp,JSON.stringify({cwd,session}));await fs.rename(tmp,file);}catch(e){await fs.unlink(tmp).catch(()=>{});throw e;}}
}
