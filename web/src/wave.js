// 가사가 없는 곡에서 가사 자리에 그리는 얇은 파형 한 줄.
// 저·중·고음 에너지로 세 개의 느린 사인파 진폭을 정하고 합쳐서, 잔잔하게 일렁이는 선을 만든다.

import { approach } from './util.js';

const LAYERS = [
  { band: 'bass', cycles: 0.85, speed: 0.9, weight: 1.0 },
  { band: 'mid', cycles: 2.1, speed: 1.6, weight: 0.55 },
  { band: 'high', cycles: 4.6, speed: 2.7, weight: 0.25 },
];

export class WaveLine {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.amp = { bass: 0, mid: 0, high: 0 };
    this.phase = LAYERS.map(() => Math.random() * Math.PI * 2);
  }

  fit() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.round(this.canvas.clientWidth * dpr);
    const h = Math.round(this.canvas.clientHeight * dpr);
    if (w && h && (this.canvas.width !== w || this.canvas.height !== h)) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.dpr = dpr;
  }

  // audio: AudioEngine (bass/mid/high 0..1), accent: [r, g, b] 0..1
  draw(dt, audio, playing, accent) {
    this.fit();
    const { ctx, canvas, dpr } = this;
    const w = canvas.width, h = canvas.height;
    if (!w || !h) return;

    // 진폭은 천천히 따라가게 (잔잔함 유지), 정지하면 평평해짐
    for (const key of ['bass', 'mid', 'high']) {
      const target = playing ? Math.min(1, audio[key]) : 0;
      this.amp[key] = approach(this.amp[key], target, dt, target > this.amp[key] ? 5 : 1.6);
    }
    LAYERS.forEach((l, i) => (this.phase[i] += dt * l.speed * (playing ? 1 : 0.25)));

    const maxAmp = h * 0.32;
    const base = playing ? 0.12 : 0; // 조용한 구간에서도 아주 살짝 숨 쉬도록
    const N = 180;
    ctx.clearRect(0, 0, w, h);
    ctx.beginPath();
    for (let i = 0; i <= N; i++) {
      const u = i / N;
      const taper = Math.pow(Math.sin(Math.PI * u), 1.6); // 양 끝은 기준선으로 수렴
      let v = 0;
      LAYERS.forEach((l, k) => {
        const a = (this.amp[l.band] * 0.88 + base) * l.weight;
        v += a * Math.sin(u * Math.PI * 2 * l.cycles + this.phase[k]);
      });
      const y = h / 2 + v * taper * maxAmp * 0.55;
      const x = u * w;
      if (i === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
    }

    // 빛번짐: shadowBlur(매 프레임 블러 계산, 무거움) 대신 넓고 옅은 선을 먼저 깔고 가는 선을 얹음
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    for (const [width, alpha] of [[10, 0.07], [5, 0.14], [2, 0.9]]) {
      ctx.strokeStyle = this.gradient(w, accent, alpha);
      ctx.lineWidth = width * dpr;
      ctx.stroke();
    }
  }

  // 양 끝이 투명해지는 가로 그라데이션 (색·폭이 같으면 재사용)
  gradient(w, accent, alpha) {
    const key = `${w}|${accent.map((c) => c.toFixed(3)).join()}|${alpha}`;
    this.grads ??= new Map();
    let g = this.grads.get(key);
    if (!g) {
      if (this.grads.size > 12) this.grads.clear();
      const [r, gr, b] = accent.map((c) => Math.round(c * 255));
      g = this.ctx.createLinearGradient(0, 0, w, 0);
      g.addColorStop(0, `rgba(${r},${gr},${b},0)`);
      g.addColorStop(0.18, `rgba(${r},${gr},${b},${alpha})`);
      g.addColorStop(0.82, `rgba(${r},${gr},${b},${alpha})`);
      g.addColorStop(1, `rgba(${r},${gr},${b},0)`);
      this.grads.set(key, g);
    }
    return g;
  }
}
