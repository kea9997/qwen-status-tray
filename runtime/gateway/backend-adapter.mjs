import {loopbackUrl} from './runtime-config.mjs';

// Generation and tool-call SSE remain OpenAI-compatible and are forwarded unchanged.
// Only token counting differs between the supported backends.
export async function countTokens({upstream, backend, model, messages, signal, timeoutMs=30000}) {
  const origin=loopbackUrl(upstream,'upstream');
  if(!Array.isArray(messages))throw Error('messages required for token counting');
  let endpoint,payload;
  if(backend==='ninfer'){
    if(messages.some(m=>!['system','user','assistant'].includes(m.role)||typeof m.content!=='string'))throw Error('NInfer token counting supports text system/user/assistant messages only.');
    endpoint='/v1/messages/count_tokens';
    const system=messages.filter(m=>m.role==='system').map(m=>m.content).join('\n');
    payload={model,messages:messages.filter(m=>m.role!=='system').map(m=>({role:m.role,content:m.content})),...(system?{system}:{})};
  }else if(backend==='vllm'){
    endpoint='/tokenize';
    payload={model,messages,add_generation_prompt:true,chat_template_kwargs:{enable_thinking:false}};
  }else throw Error('Unsupported token-counting backend: '+backend);
  const timeout=AbortSignal.timeout(timeoutMs);
  const response=await fetch(origin+endpoint,{method:'POST',headers:{'content-type':'application/json'},signal:signal?AbortSignal.any([signal,timeout]):timeout,body:JSON.stringify(payload)});
  if(!response.ok)throw Error('토큰 계산 실패: '+response.status);
  const data=await response.json(),count=backend==='ninfer'?data.input_tokens:data.count;
  if(!Number.isSafeInteger(count)||count<0)throw Error('Invalid token count');
  return count;
}
