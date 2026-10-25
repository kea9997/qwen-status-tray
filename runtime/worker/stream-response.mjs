// Parse streamed UTF-8 SSE without adding inference requests.
export async function readStream(body, onPreview, onFirstToken=()=>{}) {
  const decoder=new TextDecoder();let pending='',content='',usage,timings,finish=null,done=false,last=0,first=false;const tools=new Map();
  async function line(value){
    if(!value.startsWith('data:'))return;
    const payload=value.slice(5).trim();if(!payload)return;
    if(payload==='[DONE]'){done=true;return;}
    const event=JSON.parse(payload);if(event.error)throw Error(event.error.message||'Stream error');
    if(event.usage)usage=event.usage;
    if(event.timings)timings=event.timings;
    for(const delta of event.choices?.[0]?.delta?.tool_calls||[]){
      if(!Number.isInteger(delta.index)||delta.index<0)throw Error('Invalid streamed tool-call index');
      const call=tools.get(delta.index)||{id:'',type:'function',function:{name:'',arguments:''}};
      if(delta.id)call.id+=delta.id;if(delta.type)call.type=delta.type;
      if(delta.function?.name)call.function.name+=delta.function.name;
      if(delta.function?.arguments)call.function.arguments+=delta.function.arguments;
      tools.set(delta.index,call);
    }
    const choice=event.choices?.[0];if(!first&&(choice?.delta?.content||choice?.delta?.tool_calls?.length)){first=true;onFirstToken();}if(choice?.delta?.content)content+=choice.delta.content;
    if(choice?.finish_reason)finish=choice.finish_reason;
    if(content&&Date.now()-last>=1000){last=Date.now();await onPreview(content);}
  }
  for await(const chunk of body){pending+=decoder.decode(chunk,{stream:true});let i;while((i=pending.indexOf('\n'))>=0){await line(pending.slice(0,i).replace(/\r$/,''));pending=pending.slice(i+1);}}
  pending+=decoder.decode();if(pending.trim())await line(pending.trim());
  if(!done||!finish)throw Error('Stream ended before completion; partial output is not a finished artifact.');
  await onPreview(content);
  return {choices:[{message:{content,...(tools.size?{tool_calls:[...tools.entries()].sort(([a],[b])=>a-b).map(([,call])=>call)}:{})},finish_reason:finish}],usage,...(timings?{timings}:{})};
}
