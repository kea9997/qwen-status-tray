// Bound multimodal requests and keep binary image data out of monitor logs.
export function inspectMedia(messages) {
 let images=0, videos=0;
 for (const m of messages) for (const p of Array.isArray(m.content)?m.content:[]) {
  if(p.type==='image_url') images++;
  if(p.type==='video_url'||p.type==='input_video') videos++;
 }
 if(videos) throw Error('동영상 직접 입력은 아직 검증되지 않았습니다. 우선 화면을 사진 한 장으로 보내주세요.');
 if(images>1) throw Error('현재 사진은 대화 문맥 전체에서 한 장만 지원합니다. /new로 새 대화를 시작한 뒤 사진 한 장을 보내주세요.');
 return {images};
}
export function logMessages(messages) {
 return messages.map(m=>({...m,content:Array.isArray(m.content)?m.content.map(p=>p.type==='image_url'?{type:'image_url',image_url:{url:'[이미지 첨부: 원본 데이터는 로그에 저장하지 않음]'}}:p):m.content}));
}
