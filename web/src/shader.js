// 화면 전체를 한 번에 그리는 셰이더
//  - 배경: 미리 블러해 둔 앨범아트를 어둡고 차분하게, 중음에 맞춰 아주 살짝 숨 쉬는 줌
//  - 앨범아트: 원본 색, 살짝 둥근 모서리와 그림자
//  - 곡 전환: 잉크 번짐 (부드러운 노이즈 얼룩 + 약한 크로스페이드)

export const VERT = `#version 300 es
in vec2 aPos;
void main() { gl_Position = vec4(aPos, 0.0, 1.0); }
`;

export const FRAG = `#version 300 es
precision highp float;
out vec4 outColor;

uniform vec2  uRes;
uniform float uTime;
uniform float uDpr;

uniform sampler2D uBg;
uniform sampler2D uBgPrev;
uniform sampler2D uArt;
uniform sampler2D uArtPrev;
uniform vec4  uCrop;       // 현재 아트 크롭 (offset.xy, scale.xy)
uniform vec4  uCropPrev;
uniform float uMix;        // 전환 진행도 0..1
uniform float uSeed;       // 전환마다 바뀌는 잉크 무늬 시드
uniform float uHasArt;     // 아트 표시 정도 (곡 없음·배경 전용 모니터는 0)
uniform vec4  uRect;       // 아트 사각형 x, y, w, h (디바이스 px, 좌상단 기준)
uniform float uMid;        // 중음 에너지 0..1
uniform float uPause;      // 일시정지 정도 0..1

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }
float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

float valueNoise(vec2 p) {
  vec2 i = floor(p), f = fract(p);
  vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // 5차 보간: 격자 자국 없이 매끈하게
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
             mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

// 크고 부드러운 얼룩 (0..1)
float inkNoise(vec2 p) {
  return valueNoise(p) * 0.68 + valueNoise(p * 2.1 + 11.7) * 0.32;
}

// 모서리가 r만큼 둥근 사각형까지의 거리
float sdRoundBox(vec2 p, vec2 b, float r) {
  vec2 d = abs(p) - b + r;
  return length(max(d, 0.0)) + min(max(d.x, d.y), 0.0) - r;
}

vec3 background(vec2 uv) {
  float zoom = 1.18 + uMid * 0.025;
  vec2 drift = vec2(sin(uTime * 0.031), cos(uTime * 0.023)) * 0.025;
  vec2 q = (uv - 0.5) / zoom + 0.5 + drift;
  vec3 c = texture(uBg, q).rgb;
  if (uMix < 1.0) c = mix(texture(uBgPrev, q).rgb, c, smoothstep(0.0, 1.0, uMix));
  c = mix(vec3(luma(c)), c, 0.28) * 0.42;                // 채도·밝기 낮춤
  float vig = smoothstep(1.25, 0.25, length((uv - 0.5) * vec2(uRes.x / uRes.y, 1.0) * 0.9));
  return c * mix(0.35, 1.0, vig);
}

vec3 sampleArt(sampler2D tex, vec4 crop, vec2 uv) {
  return texture(tex, crop.xy + uv * crop.zw).rgb;
}

void main() {
  vec2 p = vec2(gl_FragCoord.x, uRes.y - gl_FragCoord.y);   // 좌상단 원점 px
  float px = uDpr;                                          // CSS 1px 의 디바이스 px
  vec3 col = background(p / uRes);

  // ── 앨범아트 ──
  vec2 center = uRect.xy + uRect.zw * 0.5;
  vec2 halfSize = uRect.zw * 0.5;
  float d = sdRoundBox(p - center, halfSize, min(halfSize.x, halfSize.y) * 0.03);

  float shadow = exp(-max(d, 0.0) / (70.0 * px)) * 0.65 * uHasArt;
  col *= 1.0 - shadow * step(0.0, d);

  if (d < 0.0 && uHasArt > 0.001) {
    vec2 local = (p - center) / (halfSize * 2.0) + 0.5;
    vec3 art = sampleArt(uArt, uCrop, local);

    // 잉크 번짐 (전환 중일 때만 계산): 시작·끝을 느리게, 넓은 경계로 스며들듯
    if (uMix < 1.0) {
      vec3 prev = sampleArt(uArtPrev, uCropPrev, local);
      float k = uMix * uMix * (3.0 - 2.0 * uMix);
      float n = inkNoise(local * 2.6 + uSeed * 37.0);
      float th = k * 1.5 - 0.25;                            // 0 → 전부 이전, 1 → 전부 새 앨범
      art = mix(prev, art, mix(smoothstep(th + 0.22, th - 0.22, n), k, 0.3));
    }

    art *= 1.0 - smoothstep(-26.0 * px, 0.0, d) * 0.18;    // 테두리 안쪽 미세한 어두움
    col = mix(col, art, smoothstep(0.0, -1.5 * px, d) * uHasArt);
  }

  col = mix(col, vec3(luma(col)) * 0.62, uPause * 0.7);    // 일시정지: 채도·밝기 하강
  col += (hash(p) - 0.5) * (1.5 / 255.0);                   // 정적 디더 (블러 배경 색 띠 방지)
  outColor = vec4(col, 1.0);
}
`;
