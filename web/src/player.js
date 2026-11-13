// 호스트 메시지로 갱신되는 재생 상태 + 재생 위치 시계.
// 화면 조작은 하지 않고, 바뀐 점은 콜백(on.*)으로 알린다.

export class Player {
  constructor(on = {}) {
    this.on = on; // { track(msg), art(msg), lyrics(msg), visibility(hidden) }
    this.key = null;
    this.playback = 'stopped';
    this.tl = { pos: 0, dur: 0, at: performance.now() };
    this.lyricsStatus = null; // null | 'loading' | 'found' | 'none' | 'instrumental' | 'error'
    this.settings = { lyrics: false, fps: 60 };
    this.hidden = false;
  }

  get playing() {
    return this.playback === 'playing';
  }

  // 마지막 타임라인 이후 흐른 시간만큼 앞으로 (일시정지면 멈춤)
  position() {
    const { pos, dur, at } = this.tl;
    let p = pos + (this.playing ? performance.now() - at : 0);
    if (dur > 0) p = Math.min(p, dur);
    return Math.max(0, p);
  }

  handle(msg) {
    switch (msg.type) {
      case 'track':
        if (msg.key === this.key) return;
        this.key = msg.key;
        this.lyricsStatus = this.settings.lyrics ? 'loading' : null;
        this.on.track?.(msg);
        return;
      case 'art':
        if (this.key && msg.key && msg.key !== this.key) return;
        this.on.art?.(msg);
        return;
      case 'playback': {
        // 상태가 바뀌는 순간의 위치로 기준점을 다시 잡는다 (재개 시 위치가 튀지 않게)
        const pos = this.position();
        this.playback = msg.state;
        this.tl = { ...this.tl, pos, at: performance.now() };
        return;
      }
      case 'timeline':
        this.tl = { pos: msg.position || 0, dur: msg.duration || 0, at: performance.now() };
        return;
      case 'lyrics':
        if (msg.key && msg.key !== this.key) return;
        this.lyricsStatus = msg.status;
        this.on.lyrics?.(msg);
        return;
      case 'audio':
        if (msg.b) this.on.audio?.(msg.b);
        return;
      case 'settings': {
        const wasLyrics = this.settings.lyrics;
        if ('lyrics' in msg) this.settings.lyrics = !!msg.lyrics;
        if ('fps' in msg) this.settings.fps = msg.fps || 60;
        if (this.settings.lyrics && !wasLyrics && this.lyricsStatus == null) this.lyricsStatus = 'loading';
        return;
      }
      case 'visibility':
        this.hidden = !!msg.hidden;
        this.on.visibility?.(this.hidden);
        return;
    }
  }
}
