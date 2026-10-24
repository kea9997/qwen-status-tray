// Ephemeral, text-only identity fixture for setup.ps1 wiring. Never loads an engine or accepts inference.
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
const [stateFile,addressFile]=process.argv.slice(2);
if(!stateFile||!addressFile||![stateFile,addressFile].every(path.isAbsolute))throw Error('Absolute fixture paths required');
const server=http.createServer(async(req,res)=>{
 try{
  const state=JSON.parse(await fs.readFile(stateFile,'utf8'));
  const data=req.url==='/health'?state.health:req.url==='/v1/models'?state.models:null;
  if(!data){res.writeHead(404);return res.end();}
  res.writeHead(200,{'content-type':'application/json'});res.end(JSON.stringify(data));
 }catch{res.writeHead(500);res.end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
await fs.writeFile(addressFile+'.tmp',JSON.stringify({origin:'http://127.0.0.1:'+server.address().port}));
await fs.rename(addressFile+'.tmp',addressFile);
