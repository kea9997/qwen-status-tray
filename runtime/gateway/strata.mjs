import {loopbackUrl} from './runtime-config.mjs';

const tokenLimit=n=>Number.isSafeInteger(n)&&n>4096?n:null;
const metric=n=>typeof n==='number'&&Number.isFinite(n)&&n>=0?n:null;
export async function strataJson(upstream,endpoint,{signal,timeoutMs=2500,...options}={}){
  const response=await fetch(loopbackUrl(upstream,'upstream')+endpoint,{...options,redirect:'error',signal:signal?AbortSignal.any([signal,AbortSignal.timeout(timeoutMs)]):AbortSignal.timeout(timeoutMs)});
  if(!response.ok){const error=Error('Strata '+endpoint+': HTTP '+response.status);error.status=response.status;throw error;}
  return response.json();
}
export function strataIdentity(health,models,expectedModel,installedContext=null){
  if(health?.service!=='strata'||typeof health.loaded!=='boolean')throw Error('Invalid Strata health response');
  const model=health.model;
  if(typeof model!=='string'||!model.trim()||/[\r\n]/.test(model))throw Error('Invalid Strata served model');
  if(expectedModel&&model!==expectedModel)throw Error('Strata served model differs from configured model');
  const listed=models?.data?.find(m=>m.id===model);
  if(models&&!listed)throw Error('Strata model is not listed by /v1/models');
  const limits=[tokenLimit(health.max_context),tokenLimit(listed?.meta?.n_ctx),tokenLimit(installedContext)].filter(n=>n!==null);
  if(!limits.length)throw Error('Strata context capacity is unknown; explicit verified profile or reported context required');
  // Upstream image availability is not gateway support: its count_tokens conversion is text-only.
  return {backend:'strata',model,loaded:health.loaded,contextTokens:Math.min(...limits),vision:false};
}
export async function readStrata(upstream,model,installedContext,signal){
  const health=await strataJson(upstream,'/health',{signal});
  return strataIdentity(health,null,model,installedContext);
}
export function normalizeStrataMetrics(data){
  if(!data||!data.engine||!data.live||!data.totals)throw Error('Invalid Strata JSON metrics');
  const {engine,live,totals}=data;
  const busy=['reading','generating'].includes(live.state);
  const running=metric(live.running)??(busy?1:live.state==='idle'||live.state==='unloaded'?0:null);
  return {backend:'strata',model:typeof engine.model==='string'?engine.model:null,state:live.state,
    context_tokens:tokenLimit(engine.max_context),loaded:live.state==='unloaded'?false:['idle','reading','generating'].includes(live.state)?true:null,
    running,waiting:metric(live.queued),input_tok_s:metric(live.prefill_tok_s_mean),output_tok_s:metric(live.tok_s_mean)??metric(live.tok_s),
    prompt_tokens:metric(totals.prompt_tokens),output_tokens:metric(totals.output_tokens),since:metric(totals.since),hardware:data.hardware??null};
}
// A narrow compatibility view for existing text-metric consumers. Missing gauges stay absent, not zero.
export function strataPrometheus(data){
  const m=normalizeStrataMetrics(data),names={running:'num_requests_running',waiting:'num_requests_waiting',prompt_tokens:'prompt_tokens_total',output_tokens:'generation_tokens_total',input_tok_s:'prompt_tokens_per_second',output_tok_s:'generation_tokens_per_second'};
  return Object.entries(names).filter(([key])=>m[key]!==null).map(([key,name])=>'vllm:'+name+' '+m[key]+'\n').join('');
}
// Pinned source v0.2.3.1 has count_tokens but no /tokenize. Convert OpenAI text/tools; no images.
export function strataCountPayload(model,messages,tools){
  const system=[],converted=[];
  for(const message of messages){
    if(typeof message.content!=='string'&&message.content!==null&&message.content!==undefined)throw Error('Strata fallback token count requires text messages; image counting unavailable');
    if(message.role==='system'){system.push(message.content||'');continue;}
    if(message.role==='tool'){converted.push({role:'user',content:[{type:'tool_result',tool_use_id:message.tool_call_id,content:message.content||''}]});continue;}
    if(!['user','assistant'].includes(message.role))throw Error('Unsupported Strata message role');
    const content=[];
    if(message.content)content.push({type:'text',text:message.content});
    for(const call of message.tool_calls||[]){
      if(call.type!=='function')throw Error('Unsupported Strata tool type');
      content.push({type:'tool_use',id:call.id,name:call.function.name,input:JSON.parse(call.function.arguments||'{}')});
    }
    converted.push({role:message.role,content:content.length?content:[{type:'text',text:''}]});
  }
  return {model,messages:converted,...(system.length?{system:system.join('\n')}:{}),...(tools?.length?{tools:tools.map(t=>({name:t.function.name,description:t.function.description||'',input_schema:t.function.parameters||{type:'object'}}))}:{}),thinking:{type:'disabled'}};
}
