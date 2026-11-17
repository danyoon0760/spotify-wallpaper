// 호스트(WebView2) 연결. 브라우저에서 열면 null을 돌려주고 목업을 쓴다.
//
// 호스트 → 웹 메시지
//   { type: 'track', key, title, artist, album }
//   { type: 'art', key, src }                                  // 이미지 주소 (호스트: https://art.wallpaper.example/…, 목업: data URL)
//   { type: 'playback', state: 'playing'|'paused'|'stopped' }
//   { type: 'timeline', position, duration }                   // ms
//   { type: 'lyrics', key, status, lines: [{ t, text }] }      // status: found|none|instrumental|error
//   { type: 'audio', b: number[64] }                           // 0..1 로그 간격 스펙트럼 (요청한 주기로만 옴)
//   { type: 'settings', lyrics, fps, lyricsOffset }            // lyricsOffset: ms, 양수 = 가사가 더 빨리
//   { type: 'visibility', hidden }
// 웹 → 호스트
//   { type: 'ready' }
//   { type: 'audio', rate }                                    // 필요한 오디오 분석 주기(Hz). 0 = 필요 없음

export function connectHost(handler) {
  const wv = window.chrome && window.chrome.webview;
  if (!wv) return null;
  wv.addEventListener('message', (e) => handler(e.data));
  const send = (msg) => wv.postMessage(msg);
  send({ type: 'ready' });
  return { send };
}
