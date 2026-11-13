// 앨범아트에서 "포인트 색" 하나를 뽑는다.
// 채도가 높고 너무 어둡거나 밝지 않은 색상군 중 면적이 넓은 것을 고르고,
// 배경화면에서 쓰기 좋은 밝기/채도로 보정한다.

function rgbToHsl(r, g, b) {
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h;
  if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
  else if (max === g) h = (b - r) / d + 2;
  else h = (r - g) / d + 4;
  return [h / 6, s, l];
}

function hslToRgb(h, s, l) {
  if (s === 0) return [l, l, l];
  const hue = (p, q, t) => {
    if (t < 0) t += 1;
    if (t > 1) t -= 1;
    if (t < 1 / 6) return p + (q - p) * 6 * t;
    if (t < 1 / 2) return q;
    if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
    return p;
  };
  const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
  const p = 2 * l - q;
  return [hue(p, q, h + 1 / 3), hue(p, q, h), hue(p, q, h - 1 / 3)];
}

const MONO_ACCENT = [0.86, 0.84, 0.8];

export function extractPalette(img) {
  const size = 48;
  const c = document.createElement('canvas');
  c.width = c.height = size;
  const ctx = c.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(img, 0, 0, size, size);
  const { data } = ctx.getImageData(0, 0, size, size);

  const BINS = 24;
  const score = new Float32Array(BINS);
  const acc = Array.from({ length: BINS }, () => [0, 0, 0, 0]);
  let chromaTotal = 0;

  for (let i = 0; i < data.length; i += 4) {
    const r = data[i] / 255, g = data[i + 1] / 255, b = data[i + 2] / 255;
    const [h, s, l] = rgbToHsl(r, g, b);
    const chroma = Math.max(r, g, b) - Math.min(r, g, b);
    chromaTotal += chroma;
    // 채도 높고 중간 밝기일수록 가중치↑
    const w = chroma * chroma * (1 - Math.min(1, Math.abs(l - 0.55) * 1.7)) * s;
    if (w <= 0) continue;
    const bin = Math.floor(h * BINS) % BINS;
    score[bin] += w;
    const a = acc[bin];
    a[0] += r * w; a[1] += g * w; a[2] += b * w; a[3] += w;
  }

  const avgChroma = chromaTotal / (size * size);
  let best = -1, bestScore = 0;
  for (let i = 0; i < BINS; i++) {
    // 이웃 색상군도 조금 합산해 경계에서 끊기지 않게
    const s = score[i] + 0.5 * (score[(i + 1) % BINS] + score[(i + BINS - 1) % BINS]);
    if (s > bestScore) { bestScore = s; best = i; }
  }

  // 거의 흑백인 앨범은 따뜻한 회백색을 포인트로
  if (best < 0 || avgChroma < 0.06 || bestScore < 0.4) {
    return { accent: MONO_ACCENT, mono: true };
  }

  const a = acc[best];
  let [h, s, l] = rgbToHsl(a[0] / a[3], a[1] / a[3], a[2] / a[3]);
  s = Math.min(0.85, Math.max(0.5, s * 1.1));
  l = Math.min(0.68, Math.max(0.56, l));
  return { accent: hslToRgb(h, s, l), mono: false };
}
