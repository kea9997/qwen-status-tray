import {loopbackUrl} from './runtime-config.mjs';
import {strataCountPayload} from './strata.mjs';

// Generation and tool-call SSE remain OpenAI-compatible and are forwarded unchanged.
export async function countTokens({upstream, backend, model, messages, tools, signal, timeoutMs=30000}) {
  if(backend!=='strata')throw Error('Unsupported token-counting backend: '+backend);
  const origin=loopbackUrl(upstream,'upstream');
  if(!Array.isArray(messages))throw Error('messages required for token counting');
  const payload={model,messages,...(tools?{tools}:{}),add_generation_prompt:true,reasoning_effort:'none',chat_template_kwargs:{enable_thinking:false}};
  const timeout=AbortSignal.timeout(timeoutMs);
  const options={method:'POST',redirect:'error',headers:{'content-type':'application/json'},signal:signal?AbortSignal.any([signal,timeout]):timeout};
  let response=await fetch(origin+'/tokenize',{...options,body:JSON.stringify(payload)}),fallback=false;
  if(response.status===404){
    await response.body?.cancel();fallback=true;
    response=await fetch(origin+'/v1/messages/count_tokens',{...options,body:JSON.stringify(strataCountPayload(model,messages,tools))});
  }
  if(!response.ok)throw Error('토큰 계산 실패: '+response.status);
  const data=await response.json(),count=fallback?data.input_tokens:(data.count??(Array.isArray(data.tokens)?data.tokens.length:undefined));
  if(!Number.isSafeInteger(count)||count<0)throw Error('Invalid token count');
  return count;
}
