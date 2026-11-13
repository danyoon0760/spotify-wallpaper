// WebGL2 렌더러: 텍스처 관리와 유니폼 전달만 담당 (그림 자체는 shader.js)
//  - 배경은 곡이 바뀔 때 한 번만 작게 블러해서 텍스처로 올려 둔다
//  - 전환은 이전/현재 텍스처 쌍과 진행도(mix)로 셰이더가 처리

import { VERT, FRAG } from './shader.js';

const TRANSITION_SEC = 2.4;

function compile(gl, type, src) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) || 'shader compile failed');
  return s;
}

// 정사각형이 아닌 아트(유튜브 썸네일 등)는 가운데를 정사각형으로 잘라 씀
function cropFor(w, h) {
  const a = w / h;
  if (a > 1) return [(1 - 1 / a) / 2, 0, 1 / a, 1];
  if (a < 1) return [0, (1 - a) / 2, 1, a];
  return [0, 0, 1, 1];
}

// 배경용: 아트를 화면 비율의 작은 캔버스에 그려 강하게 블러
function blurredBackground(img) {
  const w = 480, h = Math.max(120, Math.round((480 * window.innerHeight) / Math.max(1, window.innerWidth)));
  const c = document.createElement('canvas');
  c.width = w;
  c.height = h;
  const ctx = c.getContext('2d');
  const s = Math.max(w / img.width, h / img.height) * 1.1;
  const dw = img.width * s, dh = img.height * s;
  ctx.filter = 'blur(14px) saturate(1.15)';
  ctx.drawImage(img, (w - dw) / 2, (h - dh) / 2, dw, dh);
  ctx.filter = 'blur(10px)';
  ctx.drawImage(c, 0, 0);
  return c;
}

export class Renderer {
  constructor(canvas) {
    this.canvas = canvas;
    const gl = canvas.getContext('webgl2', { antialias: false, alpha: false, powerPreference: 'low-power' });
    if (!gl) throw new Error('WebGL2를 사용할 수 없습니다');
    this.gl = gl;

    const prog = gl.createProgram();
    gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, VERT));
    gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, FRAG));
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(prog));
    gl.useProgram(prog);

    // 화면을 덮는 삼각형 하나
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, 'aPos');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    this.u = {};
    const n = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
      const name = gl.getActiveUniform(prog, i).name;
      this.u[name] = gl.getUniformLocation(prog, name);
    }

    const blank = document.createElement('canvas');
    blank.width = blank.height = 2;
    const bctx = blank.getContext('2d');
    bctx.fillStyle = '#111';
    bctx.fillRect(0, 0, 2, 2);

    this.tex = {
      art: this.upload(gl.createTexture(), blank, true),
      artPrev: this.upload(gl.createTexture(), blank, true),
      bg: this.upload(gl.createTexture(), blank, false),
      bgPrev: this.upload(gl.createTexture(), blank, false),
    };
    this.crop = [0, 0, 1, 1];
    this.cropPrev = [0, 0, 1, 1];
    this.mix = 1;
    this.seed = 0;

    gl.uniform1i(this.u.uArt, 0);
    gl.uniform1i(this.u.uArtPrev, 1);
    gl.uniform1i(this.u.uBg, 2);
    gl.uniform1i(this.u.uBgPrev, 3);
    this.resize();
  }

  upload(tex, source, mip) {
    const gl = this.gl;
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, source);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    if (mip) gl.generateMipmap(gl.TEXTURE_2D);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, mip ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR);
    return tex;
  }

  get transitioning() {
    return this.mix < 1;
  }

  // 새 앨범아트로 전환 시작.
  // upgrade: 같은 곡의 고해상도 버전 → 전환 없이 현재 이미지만 교체 (진행 중인 전환은 그대로)
  setArt(img, { upgrade = false } = {}) {
    const t = this.tex;
    if (!upgrade) {
      [t.art, t.artPrev] = [t.artPrev, t.art];
      [t.bg, t.bgPrev] = [t.bgPrev, t.bg];
      this.cropPrev = this.crop;
      this.mix = 0;
      this.seed = Math.random();
    }
    this.upload(t.art, img, true);
    this.upload(t.bg, blurredBackground(img), false);
    this.crop = cropFor(img.width, img.height);
  }

  resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = Math.round(window.innerWidth * dpr);
    const h = Math.round(window.innerHeight * dpr);
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.dpr = dpr;
  }

  // s: { dt, time, rect(CSS px), hasArt, mid, pause }
  render(s) {
    const { gl, u, dpr, tex } = this;
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    this.mix = Math.min(1, this.mix + s.dt / TRANSITION_SEC);

    gl.uniform2f(u.uRes, this.canvas.width, this.canvas.height);
    gl.uniform1f(u.uTime, s.time);
    gl.uniform1f(u.uDpr, dpr);
    gl.uniform4f(u.uRect, s.rect.x * dpr, s.rect.y * dpr, s.rect.w * dpr, s.rect.h * dpr);
    gl.uniform4fv(u.uCrop, this.crop);
    gl.uniform4fv(u.uCropPrev, this.cropPrev);
    gl.uniform1f(u.uMix, this.mix);
    gl.uniform1f(u.uSeed, this.seed);
    gl.uniform1f(u.uHasArt, s.hasArt);
    gl.uniform1f(u.uMid, s.mid);
    gl.uniform1f(u.uPause, s.pause);

    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, tex.art);
    gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, tex.artPrev);
    gl.activeTexture(gl.TEXTURE2); gl.bindTexture(gl.TEXTURE_2D, tex.bg);
    gl.activeTexture(gl.TEXTURE3); gl.bindTexture(gl.TEXTURE_2D, tex.bgPrev);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }
}
