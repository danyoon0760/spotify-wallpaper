// 브라우저 개발용 목업: 호스트와 같은 메시지를 흉내 낸다.
//  - 가짜 트랙/앨범아트(절차 생성)/싱크 가사
//  - 합성 오디오 스펙트럼, 또는 드래그&드롭한 음악 파일 분석
//  - 이미지 파일을 드롭하면 현재 곡의 아트로 사용

import { bandsFromAnalyser } from './audio.js';

const TRACKS = [
  { title: '새벽 세 시의 신호', artist: 'Mock Artist', album: 'NIGHT SIGNAL', palette: ['#0d0d10', '#e2384d', '#f4efe6'], bpm: 124, dur: 192000 },
  { title: 'Paper Moon Static', artist: 'Placeholder Unit', album: 'STATIC BLOOM', palette: ['#0b1416', '#3fc1b0', '#e9f1ee'], bpm: 96, dur: 175000 },
  { title: '黄昏ノイズ', artist: 'テスト楽団', album: 'TWILIGHT', palette: ['#14100a', '#e9a23b', '#fbf3e4'], bpm: 140, dur: 201000 },
  { title: 'Monochrome', artist: 'Grayscale', album: 'NO COLOR', palette: ['#0a0a0a', '#8a8a8a', '#f0f0f0'], bpm: 110, dur: 160000 },
];

const LYRICS = [
  '',
  '불이 꺼진 방 안에 화면만 깜빡여',
  '멈춘 시계 소리가 박자처럼 들려',
  '창밖의 네온은 아직도 깨어 있고',
  '나는 너의 주파수를 찾고 있어',
  '',
  '지지직 — 잡음 사이로',
  '아주 작게 들리는 목소리',
  '한 번만 더 볼륨을 올려 줘',
  '이 밤이 끝나기 전에',
  '',
  '신호가 닿을 때까지',
  '나는 여기서 기다릴게',
  '흑백의 꿈 위에 색을 하나만',
  '너라는 색을 하나만',
  '',
  '불이 꺼진 방 안에 화면만 깜빡여',
  '이젠 그 박자가 내 심장 소리 같아',
];

function makeArt(track, i) {
  const s = 900;
  const c = document.createElement('canvas');
  c.width = c.height = s;
  const g = c.getContext('2d');
  const [ink, accent, paper] = track.palette;

  g.fillStyle = paper;
  g.fillRect(0, 0, s, s);

  // 큰 형태
  g.fillStyle = ink;
  if (i % 2 === 0) {
    g.beginPath();
    g.arc(s * 0.58, s * 0.46, s * 0.3, 0, Math.PI * 2);
    g.fill();
    g.fillStyle = accent;
    g.beginPath();
    g.arc(s * 0.58, s * 0.46, s * 0.12, 0, Math.PI * 2);
    g.fill();
  } else {
    g.save();
    g.translate(s * 0.5, s * 0.5);
    g.rotate(-0.25);
    g.fillRect(-s * 0.32, -s * 0.32, s * 0.64, s * 0.64);
    g.fillStyle = accent;
    g.fillRect(-s * 0.32, s * 0.1, s * 0.64, s * 0.08);
    g.restore();
  }

  // 집중선
  g.strokeStyle = ink;
  g.globalAlpha = 0.5;
  for (let k = 0; k < 90; k++) {
    const a = (k / 90) * Math.PI * 2;
    const r0 = s * (0.42 + Math.random() * 0.08);
    g.lineWidth = 1 + Math.random() * 3;
    g.beginPath();
    g.moveTo(s * 0.5 + Math.cos(a) * r0, s * 0.5 + Math.sin(a) * r0);
    g.lineTo(s * 0.5 + Math.cos(a) * s, s * 0.5 + Math.sin(a) * s);
    g.stroke();
  }
  g.globalAlpha = 1;

  // 하프톤 그라데이션
  g.fillStyle = ink;
  for (let y = 0; y < s; y += 14) {
    for (let x = 0; x < s; x += 14) {
      const r = Math.max(0, (y / s - 0.55) * 9);
      if (r > 0.2) { g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill(); }
    }
  }

  g.fillStyle = ink;
  g.font = `900 ${s * 0.07}px "Noto Sans JP", sans-serif`;
  g.fillText(track.album, s * 0.06, s * 0.94);
  return c.toDataURL('image/jpeg', 0.92);
}

export function startMock(emit) {
  let trackIndex = 0;
  let playing = true;
  let position = 0;
  let lastNow = performance.now();
  let stopped = false;
  let audioEl = null, analyser = null, actx = null;
  const bands = new Float32Array(64);
  const arts = TRACKS.map(makeArt);

  const lyricsFor = (track) => ({
    status: 'found',
    synced: true,
    lines: LYRICS.map((text, i) => ({ t: 4000 + i * (60000 / track.bpm) * 8, text })),
  });

  function loadTrack(i, artOverride) {
    trackIndex = (i + TRACKS.length) % TRACKS.length;
    const t = TRACKS[trackIndex];
    const key = `${t.title}|${t.artist}|${t.album}`;
    position = 0;
    emit({ type: 'track', key, title: t.title, artist: t.artist, album: t.album });
    emit({ type: 'art', key, src: artOverride || arts[trackIndex] });
    emit({ type: 'lyrics', key, ...lyricsFor(t) });
    emit({ type: 'playback', state: playing ? 'playing' : 'paused' });
  }

  function synthBands(time, track) {
    const beatLen = 60 / track.bpm;
    const phase = (time % beatLen) / beatLen;
    const beatNo = Math.floor(time / beatLen);
    const section = 0.35 + 0.65 * (0.5 + 0.5 * Math.sin((time / (beatLen * 32)) * Math.PI * 2));
    const kick = Math.exp(-phase * 9) * section;
    const snare = beatNo % 2 === 1 ? Math.exp(-phase * 12) * 0.8 * section : 0;
    const hat = Math.exp(-((phase * 2) % 1) * 18) * 0.6 * section;
    for (let i = 0; i < 64; i++) {
      const low = i < 8 ? kick * (1 - i / 10) : 0;
      const mid = i >= 8 && i < 34 ? snare * 0.7 + section * 0.25 : 0;
      const high = i >= 34 ? hat * (1 - (i - 34) / 50) + section * 0.12 : 0;
      bands[i] = Math.min(1, 0.15 + low * 0.8 + mid + high + Math.random() * 0.04);
    }
    return bands;
  }

  function tick(now) {
    const dt = (now - lastNow) / 1000;
    lastNow = now;
    const t = TRACKS[trackIndex];
    if (!stopped) {
      if (audioEl && analyser) {
        position = audioEl.currentTime * 1000;
        emit({ type: 'audio', b: Array.from(bandsFromAnalyser(analyser, actx.sampleRate, bands)) });
      } else {
        if (playing) position += dt * 1000;
        if (position > t.dur) loadTrack(trackIndex + 1);
        emit({ type: 'audio', b: playing ? Array.from(synthBands(position / 1000, t)) : new Array(64).fill(0) });
      }
    }
    requestAnimationFrame(tick);
  }

  setInterval(() => {
    if (stopped) return;
    const dur = audioEl && isFinite(audioEl.duration) ? audioEl.duration * 1000 : TRACKS[trackIndex].dur;
    emit({ type: 'timeline', position, duration: dur });
  }, 500);

  window.addEventListener('keydown', (e) => {
    if (e.code === 'Space') {
      playing = !playing;
      if (audioEl) playing ? audioEl.play() : audioEl.pause();
      emit({ type: 'playback', state: playing ? 'playing' : 'paused' });
    } else if (e.key === 'n' || e.key === 'N') {
      loadTrack(trackIndex + 1);
    } else if (e.key === 'i' || e.key === 'I') {
      stopped = !stopped;
      if (stopped) emit({ type: 'playback', state: 'stopped' });
      else loadTrack(trackIndex);
    } else if (e.key === 'ArrowRight') {
      position += 10000;
      if (audioEl) audioEl.currentTime += 10;
    }
  });

  // 드래그&드롭: 이미지 → 앨범아트, 오디오 → 실제 분석
  window.addEventListener('dragover', (e) => e.preventDefault());
  window.addEventListener('drop', (e) => {
    e.preventDefault();
    const file = e.dataTransfer.files[0];
    if (!file) return;
    const url = URL.createObjectURL(file);
    if (file.type.startsWith('image/')) {
      const reader = new FileReader();
      reader.onload = () => {
        arts[trackIndex] = reader.result;
        const t = TRACKS[trackIndex];
        emit({ type: 'art', key: `${t.title}|${t.artist}|${t.album}`, src: reader.result });
      };
      reader.readAsDataURL(file);
    } else if (file.type.startsWith('audio/')) {
      if (!actx) actx = new AudioContext();
      if (audioEl) audioEl.pause();
      audioEl = new Audio(url);
      const src = actx.createMediaElementSource(audioEl);
      analyser = actx.createAnalyser();
      analyser.fftSize = 4096;
      analyser.smoothingTimeConstant = 0.5;
      src.connect(analyser);
      analyser.connect(actx.destination);
      actx.resume();
      audioEl.play();
      playing = true;
      const t = TRACKS[trackIndex];
      const key = `${file.name}|local|${t.album}`;
      emit({ type: 'track', key, title: file.name.replace(/\.[^.]+$/, ''), artist: 'Local file', album: t.album });
      emit({ type: 'art', key, src: arts[trackIndex] });
      emit({ type: 'lyrics', key, status: 'none' });
      emit({ type: 'playback', state: 'playing' });
    }
  });

  loadTrack(0);
  requestAnimationFrame(tick);
}
