// 호스트(WebView2) 연결. 브라우저에서 열면 null을 돌려주고 목업을 쓴다.
//
// 호스트 → 웹 메시지
//   { type: 'track', key, title, artist, album }
//   { type: 'art', key, src }                                  // data URL
//   { type: 'playback', state: 'playing'|'paused'|'stopped' }
//   { type: 'timeline', position, duration }                   // ms
//   { type: 'lyrics', key, status, lines: [{ t, text }] }      // status: found|none|instrumental|error
//   { type: 'audio', b: number[64] }                           // 0..1 로그 간격 스펙트럼
//   { type: 'settings', lyrics, fps }
//   { type: 'visibility', hidden }
// 웹 → 호스트
//   { type: 'ready' }

export function connectHost(handler) {
  const wv = window.chrome && window.chrome.webview;
  if (!wv) return null;
  wv.addEventListener('message', (e) => handler(e.data));
  const send = (msg) => wv.postMessage(msg);
  send({ type: 'ready' });
  return { send };
}
