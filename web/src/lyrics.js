// 싱크 가사 뷰: 현재 줄을 세로 중앙 근처에 두고 부드럽게 스크롤, 현재 줄은 줄 단위로 밝게 표시.

import { setStyle } from './util.js';

export class LyricsView {
  constructor(root) {
    this.viewport = root.querySelector('.lyrics-viewport');
    this.track = root.querySelector('.lyrics-track');
    this.lines = [];
    this.els = [];
    this.index = -2;
    this.gen = 0; // set() 호출 세대 (페이드 도중 새 가사가 오면 이전 예약은 무시)
  }

  get hasLines() {
    return this.lines.length > 0;
  }

  // data: { status: 'found'|'none'|'instrumental'|'error', lines: [{ t, text }] }
  // 기존 가사는 살짝 페이드아웃한 뒤 교체하고, 새 가사는 아래에서 떠오르듯 페이드인
  set(data) {
    const gen = ++this.gen;
    if (this.els.length) {
      this.viewport.classList.add('fade-out');
      setTimeout(() => gen === this.gen && this.build(data), 350);
    } else {
      this.build(data);
    }
  }

  build(data) {
    this.track.innerHTML = '';
    this.els = [];
    this.lines = [];
    this.index = -2;
    this.viewport.classList.add('fade-out');
    if (!data || data.status !== 'found' || !data.lines?.length) return;

    this.lines = data.lines.map((l) => ({ t: l.t, text: (l.text || '').trim() }));
    const frag = document.createDocumentFragment();
    for (const line of this.lines) {
      const el = document.createElement('div');
      el.className = line.text ? 'line' : 'line gap';
      el.textContent = line.text || '· · ·';
      frag.appendChild(el);
      this.els.push(el);
    }
    this.track.appendChild(frag);

    // 첫 배치(스크롤 위치)는 애니메이션 없이 잡고, 다음 프레임에 페이드인 시작
    this.track.style.transition = 'none';
    this.layout();
    requestAnimationFrame(() => {
      this.track.style.transition = '';
      requestAnimationFrame(() => this.viewport.classList.remove('fade-out'));
    });
  }

  update(posMs) {
    if (!this.lines.length) return;

    // 시작 시간이 posMs 이하인 마지막 줄 (이진 탐색)
    let lo = 0, hi = this.lines.length - 1, idx = -1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (this.lines[mid].t <= posMs) { idx = mid; lo = mid + 1; } else hi = mid - 1;
    }
    if (idx === this.index) return;

    this.els[this.index]?.classList.remove('active');
    this.index = idx;
    this.els[idx]?.classList.add('active');
    this.layout();
  }

  layout() {
    if (!this.els.length) return;
    const idx = this.index;
    this.els.forEach((el, i) => {
      if (i === idx) {
        setStyle(el, 'opacity', '1');
        setStyle(el, 'transform', 'scale(1)');
        setStyle(el, 'filter', '');
        return;
      }
      const dist = i - (idx < 0 ? -0.6 : idx);
      const a = Math.abs(dist);
      setStyle(el, 'transform', 'scale(0.9)');
      // 화면(마스크) 밖 줄은 그냥 숨김 — blur 필터는 줄마다 별도 합성 레이어를 만들어 비쌈
      if (a > 6) {
        setStyle(el, 'opacity', '0');
        setStyle(el, 'filter', 'none');
        return;
      }
      const base = dist < 0 ? 0.26 : 0.46; // 지나간 줄은 더 흐리게
      setStyle(el, 'opacity', Math.max(0.05, base - (a - 1) * 0.085).toFixed(3));
      setStyle(el, 'filter', `blur(${(Math.min(a, 4) * 0.45).toFixed(2)}px)`);
    });
    const target = this.els[Math.max(idx, 0)];
    const vh = this.viewport.clientHeight;
    const y = vh * 0.42 - (target.offsetTop + target.offsetHeight / 2) + (idx < 0 ? vh * 0.08 : 0);
    this.track.style.transform = `translate3d(0, ${y.toFixed(1)}px, 0)`;
  }
}
