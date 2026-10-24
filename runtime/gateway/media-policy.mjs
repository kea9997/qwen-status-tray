// Refuse unsupported media and keep binary image data out of monitor logs.
export function inspectMedia(messages) {
 let images=0, videos=0;
 for (const m of messages) for (const p of Array.isArray(m.content)?m.content:[]) {
  if(p.type==='image_url') images++;
  if(p.type==='video_url'||p.type==='input_video') videos++;
 }
 if(videos) throw Error('Strata 게이트웨이는 텍스트·도구 호출 전용입니다. 동영상 입력은 지원하지 않습니다.');
 if(images>1) throw Error('Strata 게이트웨이는 텍스트·도구 호출 전용입니다. 이미지 입력은 지원하지 않습니다.');
 return {images};
}
export function logMessages(messages) {
 return messages.map(m=>({...m,content:Array.isArray(m.content)?m.content.map(p=>p.type==='image_url'?{type:'image_url',image_url:{url:'[이미지 첨부: 원본 데이터는 로그에 저장하지 않음]'}}:p):m.content}));
}
