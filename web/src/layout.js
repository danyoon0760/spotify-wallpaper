// 화면 크기(CSS px)에 따른 배치
//  - centered: 가사 꺼짐 — 앨범 가운데
//  - lyrics:   가사 켜짐 — 앨범 왼쪽 (가사/파형은 lyricsBox)

import { lerp } from './util.js';

export function layoutFor(W, H) {
  const cSize = Math.min(H * 0.64, W * 0.5);
  const lSize = Math.min(H * 0.58, W * 0.36);
  const lift = H * 0.025; // 아래쪽 제목 공간만큼 살짝 위로
  return {
    centered: { x: (W - cSize) / 2, y: (H - cSize) / 2 - lift, w: cSize, h: cSize },
    lyrics: { x: W * 0.3 - lSize / 2, y: (H - lSize) / 2 - lift, w: lSize, h: lSize },
    lyricsBox: { x: W * 0.5, y: (H - lSize) / 2 - lift, w: W * 0.4, h: lSize },
  };
}

export function lerpRect(a, b, t) {
  return { x: lerp(a.x, b.x, t), y: lerp(a.y, b.y, t), w: lerp(a.w, b.w, t), h: lerp(a.h, b.h, t) };
}
