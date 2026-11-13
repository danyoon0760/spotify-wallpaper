// 개발용: 실행 중인 배경화면 페이지에서 JS 식을 평가하거나 스크린샷을 찍는다.
// 사용: node dev/cdp-eval.mjs "<expression>" [port]
//       node dev/cdp-eval.mjs --shot out.png [port]
import { writeFileSync } from 'node:fs';

const shot = process.argv[2] === '--shot';
const expr = shot ? null : process.argv[2] || 'document.title';
const out = shot ? process.argv[3] : null;
const port = process.argv[shot ? 4 : 3] || 9229;

const targets = await (await fetch(`http://127.0.0.1:${port}/json`)).json();
// 모니터가 여러 개면 페이지도 여러 개: CDP_PAGE=bg 로 배경 전용 페이지 선택 (기본은 메인)
const wantBg = process.env.CDP_PAGE === 'bg';
const page = targets.find((t) => t.type === 'page' && t.url.includes('wallpaper.example') && t.url.includes('mode=background') === wantBg);
if (!page) { console.error('page not found', targets.map((t) => t.url)); process.exit(1); }

const ws = new WebSocket(page.webSocketDebuggerUrl);
ws.onopen = () => ws.send(JSON.stringify(shot
  ? { id: 1, method: 'Page.captureScreenshot', params: { format: 'png' } }
  : { id: 1, method: 'Runtime.evaluate', params: { expression: expr, returnByValue: true, awaitPromise: true } }));
ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  if (msg.id !== 1) return;
  if (shot) {
    writeFileSync(out, Buffer.from(msg.result.data, 'base64'));
    console.log('saved', out);
  } else {
    const r = msg.result?.result;
    console.log(r?.value !== undefined ? JSON.stringify(r.value, null, 2) : JSON.stringify(msg.result, null, 2));
  }
  ws.close();
};
