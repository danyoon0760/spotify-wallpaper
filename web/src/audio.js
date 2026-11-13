// 호스트(또는 목업)가 보내는 64밴드 스펙트럼(0..1, 로그 간격)을
// 시각화용 저·중·고음 에너지(0..1)로 가공한다. 곡·볼륨이 달라도 폭이 비슷하도록 자동 게인을 건다.

import { clamp01 } from './util.js';

const BANDS = 64;
const RANGES = { bass: [0, 7], mid: [7, 30], high: [30, BANDS] };

function mean(arr, from, to) {
  let s = 0;
  for (let i = from; i < to; i++) s += arr[i];
  return s / (to - from);
}

// 올라갈 땐 attack, 내려갈 땐 release 속도로 따라가는 엔벨로프
function follow(cur, target, dt, attack, release) {
  const k = target > cur ? attack : release;
  return cur + (target - cur) * (1 - Math.exp(-dt * k));
}

export class AudioEngine {
  constructor() {
    this.bands = new Float32Array(BANDS);
    this.lastInputAt = -1e9;
    this.peak = { bass: 0.25, mid: 0.25, high: 0.25 };
    this.bass = 0;
    this.mid = 0;
    this.high = 0;
  }

  // 스펙트럼 프레임 입력 (보통 ~60Hz)
  setBands(arr) {
    for (let i = 0; i < BANDS; i++) this.bands[i] = arr[i] || 0;
    this.lastInputAt = performance.now();
  }

  update(dt, now) {
    // 입력이 끊기면 서서히 0으로
    if (now - this.lastInputAt > 250) {
      for (let i = 0; i < BANDS; i++) this.bands[i] *= Math.exp(-dt * 6);
    }
    const speeds = { bass: [28, 5], mid: [14, 3], high: [30, 7] };
    for (const [key, [from, to]] of Object.entries(RANGES)) {
      const raw = mean(this.bands, from, to);
      this.peak[key] = Math.max(raw, this.peak[key] - dt * 0.06, 0.08);
      this[key] = follow(this[key], clamp01(raw / this.peak[key]), dt, ...speeds[key]);
    }
  }
}

// 브라우저 개발용: AnalyserNode 데이터를 호스트와 같은 64밴드 포맷으로 변환
let analyserBuf = null;
export function bandsFromAnalyser(analyser, sampleRate, out) {
  const bins = analyser.frequencyBinCount;
  if (!analyserBuf || analyserBuf.length !== bins) analyserBuf = new Float32Array(bins);
  analyser.getFloatFrequencyData(analyserBuf);
  const fftSize = bins * 2;
  const fMin = 25, fMax = 16000;
  for (let i = 0; i < BANDS; i++) {
    const f0 = fMin * Math.pow(fMax / fMin, i / BANDS);
    const f1 = fMin * Math.pow(fMax / fMin, (i + 1) / BANDS);
    const k0 = Math.floor((f0 * fftSize) / sampleRate);
    const k1 = Math.max(k0 + 1, Math.ceil((f1 * fftSize) / sampleRate));
    let best = -200;
    for (let k = k0; k < k1 && k < bins; k++) best = Math.max(best, analyserBuf[k]);
    const tilted = best + 2.5 * Math.log2(Math.sqrt(f0 * f1) / 1000);
    out[i] = clamp01((tilted + 78) / 60);
  }
  return out;
}
