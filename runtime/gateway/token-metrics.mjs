// Based on the reviewed local Qwen draft; never infer missing usage as zero.
export function metrics(input, output, seconds, ttft) {
  const token = v => Number.isInteger(v) && v >= 0 ? v : null;
  const time = Number.isFinite(seconds) && seconds > 0 ? seconds : null;
  const first = Number.isFinite(ttft) && ttft >= 0 && time !== null && ttft <= time ? ttft : null;
  const out = token(output);
  const tpot = out > 1 && first !== null && time > first ? (time - first) / (out - 1) : null;
  return {input_tokens:token(input), output_tokens:out, request_seconds:time, ttft_seconds:first,
    effective_tok_s:out !== null && time !== null ? out/time : null,
    tpot_seconds:tpot, decode_tok_s:tpot !== null ? 1/tpot : null};
}
export function aggregate(rows) {
  const valid=rows.filter(r=>r.status==='completed' && !r.warmup && !r.quality_only && Number.isInteger(r.input_tokens) && r.input_tokens>=0 && Number.isInteger(r.output_tokens) && r.output_tokens>=0 && Number.isFinite(r.request_seconds) && r.request_seconds>0);
  const sum=k=>valid.reduce((n,r)=>n+r[k],0), seconds=sum('request_seconds');
  return {count:valid.length,input_tokens:sum('input_tokens'),output_tokens:sum('output_tokens'),request_seconds:seconds,effective_tok_s:seconds>0?sum('output_tokens')/seconds:null};
}
