// Reserve the actual generation allowance within each supported backend context.
export const CHAT_OUTPUT_TOKENS=4096;
export const BENCHMARK_OUTPUT_TOKENS=256;
export const MAX_CHAT_CHARACTERS=4_000_000;
export function chatInputBudget(backend){
  if(backend==='vllm')return 65_536-CHAT_OUTPUT_TOKENS;
  if(backend==='ninfer')return 229_376-CHAT_OUTPUT_TOKENS;
  throw Error('Unsupported context backend: '+backend);
}
// The count API includes its template. Keep another 256 tokens for backend/template variation.
export const CONTEXT_TARGETS=Object.freeze({'8k':7680,'32k':32256,'64k':65024,'224k':229_376-BENCHMARK_OUTPUT_TOKENS-256});
