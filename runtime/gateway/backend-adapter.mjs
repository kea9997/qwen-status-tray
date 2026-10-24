import {loopbackUrl} from './runtime-config.mjs';
import {strataCountPayload} from './strata.mjs';

// Generation and tool-call SSE remain OpenAI-compatible and are forwarded unchanged.
// Only token counting differs between the supported backends.
export async function countTokens({upstream, backend, model, messages, tools, signal, timeoutMs=30000}) {
  const origin=loopbackUrl(upstream,'upstream');
  if(!Array.isArray(messages))throw Error('messages required for token counting');
  let endpoint,payload;
  if(backend==='ninfer'){
    if(messages.some(m=>!['system','user','assistant'].includes(m.role)||typeof m.content!=='string'))throw Error('NInfer token counting supports text system/user/assistant messages only.');
    endpoint='/v1/messages/count_tokens';
    const system=messages.filter(m=>m.role==='system').map(m=>m.content).join('\n');
    payload={model,messages:messages.filter(m=>m.role!=='system').map(m=>({role:m.role,content:m.content})),...(system?{system}:{})};
  }else if(backend==='vllm'||backend==='strata'){
    endpoint='/tokenize';
    payload={model,messages,...(tools?{tools}:{}),add_generation_prompt:true,reasoning_effort:'none',chat_template_kwargs:{enable_thinking:false}};
  }else throw Error('Unsupported token-counting backend: '+backend);
  const timeout=AbortSignal.timeout(timeoutMs);
  const options={method:'POST',redirect:'error',headers:{'content-type':'application/json'},signal:signal?AbortSignal.any([signal,timeout]):timeout};
  let response=await fetch(origin+endpoint,{...options,body:JSON.stringify(payload)}),fallback=false;
  if(backend==='strata'&&response.status===404){
    await response.body?.cancel();fallback=true;
    response=await fetch(origin+'/v1/messages/count_tokens',{...options,body:JSON.stringify(strataCountPayload(model,messages,tools))});
  }
  if(!response.ok)throw Error('토큰 계산 실패: '+response.status);
  const data=await response.json(),count=backend==='ninfer'||fallback?data.input_tokens:backend==='strata'?(data.count??(Array.isArray(data.tokens)?data.tokens.length:undefined)):data.count;
  if(!Number.isSafeInteger(count)||count<0)throw Error('Invalid token count');
  return count;
}
