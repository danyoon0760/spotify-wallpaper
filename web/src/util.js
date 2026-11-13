// 여러 모듈에서 쓰는 작은 수학 도우미

export const clamp01 = (v) => (v < 0 ? 0 : v > 1 ? 1 : v);
export const lerp = (a, b, t) => a + (b - a) * t;
export const smooth = (t) => t * t * (3 - 2 * t);

// 프레임레이트와 무관하게 target 쪽으로 지수적으로 다가감 (speed가 클수록 빠름)
export const approach = (cur, target, dt, speed) => cur + (target - cur) * (1 - Math.exp(-dt * speed));

// 값이 바뀔 때만 스타일을 씀 (매 프레임 같은 값을 다시 쓰면 브라우저가 스타일 계산을 반복함)
const styleCache = new WeakMap();
export function setStyle(el, prop, value) {
  let cache = styleCache.get(el);
  if (!cache) styleCache.set(el, (cache = {}));
  if (cache[prop] === value) return;
  cache[prop] = value;
  if (prop.startsWith('--')) el.style.setProperty(prop, value);
  else el.style[prop] = value;
}
