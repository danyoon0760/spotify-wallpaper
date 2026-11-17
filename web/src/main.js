// 진입점: 모듈을 연결하고 매 프레임 화면을 갱신한다.
//  player   — 호스트 메시지 → 재생 상태
//  layout   — 앨범/가사 배치
//  renderer — WebGL 배경·앨범아트
//  lyrics / wave — 가사 또는 (가사 없을 때) 파형

import { Renderer } from './renderer.js';
import { AudioEngine } from './audio.js';
import { extractPalette } from './palette.js';
import { LyricsView } from './lyrics.js';
import { WaveLine } from './wave.js';
import { Player } from './player.js';
import { layoutFor, lerpRect } from './layout.js';
import { connectHost } from './bridge.js';
import { approach, smooth, setStyle } from './util.js';

const $ = (sel) => document.querySelector(sel);

// 보조 모니터용: 앨범·글자 없이 흐린 배경만 (index.html?mode=background)
const BG_ONLY = new URLSearchParams(location.search).get('mode') === 'background';
if (BG_ONLY) $('#ui').style.display = 'none';

const els = {
  meta: $('#meta'),
  title: $('.meta-title'),
  artist: $('.meta-artist'),
  fill: $('.meta-progress-fill'),
  lyrics: $('#lyrics'),
  hint: $('#dev-hint'),
};

const renderer = new Renderer($('#gl'));
const audio = new AudioEngine();
const lyricsView = new LyricsView(els.lyrics);
const wave = new WaveLine($('.wave'));

// 화면에서 부드럽게 따라가는 값들
const anim = {
  rect: null,
  lyricsMix: 0,  // 0 = 가운데 배치, 1 = 가사 배치
  hasArt: 0,
  pause: 0,
  accent: [0.86, 0.84, 0.8],
  accentTarget: [0.86, 0.84, 0.8],
  cssAccent: '',
};

let hasArt = false;
let artToken = 0;
let artShownKey = null;

const player = new Player({
  track(msg) {
    els.title.textContent = msg.title || '';
    els.artist.textContent = msg.artist || '';
    lyricsView.set(null);
  },
  art(msg) {
    const token = ++artToken;
    const img = new Image();
    // 호스트의 아트 주소는 페이지와 출처가 달라서, 이렇게 받아야 WebGL 텍스처와 색 추출에 쓸 수 있다
    img.crossOrigin = 'anonymous';
    img.onerror = () => console.warn('album art failed to load', msg.src?.slice(0, 80));
    img.onload = () => {
      if (token !== artToken) return;
      // 같은 곡에 이미 아트를 보여 줬다면(썸네일 → 고해상도 교체) 전환 효과 없이 교체
      renderer.setArt(img, { upgrade: !!msg.key && msg.key === artShownKey });
      artShownKey = msg.key;
      anim.accentTarget = extractPalette(img).accent;
      hasArt = true;
    };
    img.src = msg.src;
  },
  lyrics: (msg) => lyricsView.set(msg),
  audio: (bands) => audio.setBands(bands),
  visibility: (hidden) => { if (!hidden) requestFrame(); },
});

// ───────────── 레이아웃 & UI ─────────────

let lastLyricsHeight = 0;

function updateLayout(dt) {
  const W = window.innerWidth, H = window.innerHeight;
  // 가사 설정이 켜져 있으면 곡에 가사가 있든 없든 가사 배치 유지
  const lyricsMode = !BG_ONLY && player.settings.lyrics && player.playback !== 'stopped' && hasArt;
  const noLyrics = lyricsMode && !lyricsView.hasLines && player.lyricsStatus !== 'loading';

  const L = layoutFor(W, H);
  anim.lyricsMix = approach(anim.lyricsMix, lyricsMode ? 1 : 0, dt, 3.2);
  anim.rect = lerpRect(L.centered, L.lyrics, smooth(Math.min(1, Math.max(0, anim.lyricsMix))));
  if (BG_ONLY) return { lyricsMode, noLyrics };

  const box = L.lyricsBox;
  setStyle(els.lyrics, 'transform', `translate(${box.x}px, ${box.y}px)`);
  setStyle(els.lyrics, 'width', `${box.w}px`);
  if (box.h !== lastLyricsHeight) {
    lastLyricsHeight = box.h;
    els.lyrics.style.height = `${box.h}px`;
    lyricsView.layout();
  }
  els.lyrics.classList.toggle('show', lyricsMode && anim.lyricsMix > 0.55);
  els.lyrics.classList.toggle('no-lyrics', noLyrics);
  return { lyricsMode, noLyrics };
}

function updateUI(dt, { lyricsMode, noLyrics }) {
  const r = anim.rect;
  setStyle(els.meta, 'transform', `translate(${r.x.toFixed(1)}px, ${r.y.toFixed(1)}px)`);
  setStyle(els.meta, 'width', `${r.w.toFixed(1)}px`);
  setStyle(els.meta, 'height', `${r.h.toFixed(1)}px`);
  setStyle(els.meta, 'opacity', smooth(anim.hasArt).toFixed(3));

  const pos = player.position();
  const dur = player.tl.dur;
  setStyle(els.fill, 'transform', `scaleX(${dur ? Math.min(1, pos / dur).toFixed(4) : 0})`);

  if (lyricsMode || anim.lyricsMix > 0.01) lyricsView.update(pos + player.settings.lyricsOffset);
  if (noLyrics) wave.draw(dt, audio, player.playing, anim.accent);

  setStyle(document.documentElement, '--accent', anim.accent.map((v) => Math.round(v * 255)).join(', '));
}

// 레이어별 FPS: 움직이는 게 있을 때만 높이고 평소엔 낮춰서 GPU·CPU 절약
//  - WebGL(배경+앨범, 화면 전체): 전환·배치 이동 중에만 설정 FPS. 평소 배경 드리프트는 아주 느려 저FPS로 충분
//  - UI(파형 캔버스 등): 파형이 보이면 설정 FPS. 가사 스크롤은 CSS 전환이라 FPS와 무관
const GL_PLAYING_FPS = 24, GL_IDLE_FPS = 10;
function settling(mode) {
  return (
    renderer.transitioning ||
    Math.abs(anim.lyricsMix - (mode.lyricsMode ? 1 : 0)) > 0.002 ||
    Math.abs(anim.hasArt - (hasArt && player.playback !== 'stopped' ? 1 : 0)) > 0.002 ||
    Math.abs(anim.pause - (player.playback === 'paused' ? 1 : 0)) > 0.002
  );
}
function glFps(mode) {
  const max = player.settings.fps;
  if (settling(mode)) return max;
  // 배경 전용 모니터는 소리 반응도 없어서 아주 느리게 그려도 됨
  return Math.min(max, player.playing && !BG_ONLY ? GL_PLAYING_FPS : GL_IDLE_FPS);
}
function uiFps(mode, gl) {
  return mode.noLyrics && player.playing ? player.settings.fps : gl;
}

// 호스트에 요청할 오디오 분석 주기(Hz). 쓰는 만큼만 받는다 (호스트는 0이면 분석·캡처를 쉼)
//  - 파형이 보일 때(가사 없는 곡): 30 — 선이 소리를 따라 움직여야 함
//  - 그 밖의 재생 중: 10 — 배경이 중음에 맞춰 아주 살짝 숨 쉬는 데만 쓰임
//  - 일시정지·정지·배경 전용 모니터: 0
const AUDIO_WAVE_HZ = 30, AUDIO_AMBIENT_HZ = 10;
function audioRate(mode) {
  if (BG_ONLY || !player.playing) return 0;
  return mode.noLyrics ? AUDIO_WAVE_HZ : AUDIO_AMBIENT_HZ;
}
let host = null;
let sentAudioRate = -1;
function requestAudio(rate) {
  if (rate === sentAudioRate) return;
  sentAudioRate = rate;
  host?.send({ type: 'audio', rate });
}

// ───────────── 메인 루프 ─────────────

const t0 = performance.now();
let last = t0;
let lastDraw = 0, lastGl = 0, glDt = 0;
let rafId = 0, timerId = 0;
let fps = 60;

// 다음 프레임 예약: 그릴 때가 될 때까지 타이머로 기다렸다가 rAF를 한 번만 요청.
// (rAF를 매번 요청하고 건너뛰면 165Hz 모니터에선 초당 165번 렌더러·GPU 프로세스가 깨어남)
function requestFrame() {
  if (rafId || timerId || player.hidden) return;
  const wait = lastDraw + 1000 / fps - performance.now() - 4;
  if (wait <= 0) rafId = requestAnimationFrame(frame);
  else timerId = setTimeout(() => { timerId = 0; rafId = requestAnimationFrame(frame); }, wait);
}

function frame(now) {
  rafId = 0;
  if (player.hidden) return; // 가려져 있으면 그리지 않음 (보이면 visibility 콜백이 재개)
  lastDraw = now;
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;

  audio.update(dt, now);
  anim.hasArt = approach(anim.hasArt, hasArt && player.playback !== 'stopped' ? 1 : 0, dt, 2.2);
  anim.pause = approach(anim.pause, player.playback === 'paused' ? 1 : 0, dt, 2.5);
  for (let i = 0; i < 3; i++) anim.accent[i] = approach(anim.accent[i], anim.accentTarget[i], dt, 1.6);

  const mode = updateLayout(dt);
  if (!BG_ONLY) updateUI(dt, mode);
  requestAudio(audioRate(mode));

  // WebGL은 자기 FPS에 맞을 때만 다시 그림 (안 그리면 이전 화면이 그대로 합성됨)
  const gl = glFps(mode);
  glDt += dt;
  if (now - lastGl >= 1000 / gl - 4) {
    renderer.render({
      dt: Math.min(0.25, glDt),
      time: (now - t0) / 1000,
      rect: anim.rect,
      hasArt: BG_ONLY ? 0 : anim.hasArt,
      mid: audio.mid * (player.playing ? 1 : 0.2),
      pause: anim.pause,
    });
    lastGl = now;
    glDt = 0;
  }
  fps = Math.max(gl, uiFps(mode, gl));
  requestFrame();
}

window.addEventListener('resize', () => {
  renderer.resize();
  lyricsView.layout();
});
document.fonts?.ready.then(() => lyricsView.layout());

// ───────────── 연결 ─────────────

const handle = (msg) => player.handle(msg);
host = connectHost(handle);
if (!host) {
  // 브라우저 개발 모드
  const { startMock } = await import('./mock.js');
  startMock(handle);
  els.hint.textContent = 'MOCK  ·  L 가사  ·  K 가사 없는 곡  ·  [ ] 가사 싱크  ·  SPACE 재생/정지  ·  N 다음 곡  ·  I idle  ·  → 10초  ·  이미지/음악 파일 드롭';
  window.addEventListener('keydown', (e) => {
    if (e.key === 'l' || e.key === 'L') handle({ type: 'settings', lyrics: !player.settings.lyrics });
    if (e.key === 'k' || e.key === 'K') handle({ type: 'lyrics', key: player.key, status: 'none' });
    if (e.key === '[' || e.key === ']') {
      handle({ type: 'settings', lyricsOffset: player.settings.lyricsOffset + (e.key === ']' ? 100 : -100) });
      els.hint.textContent = `가사 싱크 ${player.settings.lyricsOffset} ms`;
    }
  });
}
window.__wp = { player, anim, audio, lyricsView, renderer }; // 디버깅용

requestFrame();
