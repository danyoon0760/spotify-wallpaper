// 화면에서 쓰는 폰트를 web/fonts 로 내려받는다 (한 번 받아서 저장소에 넣어 두므로 평소엔 다시 돌릴 필요 없음).
// 폰트 버전을 올릴 때만:  node dev/fetch-fonts.mjs
//
//  - Pretendard Variable   한글·라틴 본문            (orioncactus/pretendard, SIL OFL 1.1)
//  - Noto Sans JP          일본어 (120개 조각, 필요한 글자가 든 조각만 읽힘)   (@fontsource-variable/noto-sans-jp, SIL OFL 1.1)
//  - JetBrains Mono        가사 간주 표시 "· · ·"와 개발용 안내문            (@fontsource/jetbrains-mono, SIL OFL 1.1)
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const PRETENDARD = 'pretendard@1.3.9';
const NOTO = '@fontsource-variable/noto-sans-jp@5.3.1';
const JETBRAINS = '@fontsource/jetbrains-mono@5.3.0';
const CDN = 'https://cdn.jsdelivr.net';

const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'web', 'fonts');

async function get(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${res.status} ${url}`);
  return Buffer.from(await res.arrayBuffer());
}

async function save(rel, data) {
  const path = join(out, rel);
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, data);
  return data.length;
}

// 한 번에 몇 개씩만 받기
async function pool(items, size, fn) {
  const queue = [...items];
  await Promise.all(Array.from({ length: size }, async () => {
    while (queue.length) await fn(queue.shift());
  }));
}

let total = 0;

// ── Pretendard, JetBrains Mono: 파일 하나씩 ──
total += await save('pretendard/PretendardVariable.woff2', await get(`${CDN}/npm/${PRETENDARD}/dist/web/variable/woff2/PretendardVariable.woff2`));
total += await save('jetbrains-mono/jetbrains-mono-latin-400-normal.woff2', await get(`${CDN}/npm/${JETBRAINS}/files/jetbrains-mono-latin-400-normal.woff2`));

// ── Noto Sans JP: 원본 CSS에서 일본어 조각(숫자 이름)과 라틴 조각만 골라 받는다 ──
const notoCss = (await get(`${CDN}/npm/${NOTO}/wght.css`)).toString('utf8');
const faces = notoCss.split('@font-face').slice(1).map((block) => {
  const file = /url\(\.\/files\/([^)]+\.woff2)\)/.exec(block)?.[1];
  const range = /unicode-range:\s*([^;]+);/.exec(block)?.[1];
  return { file, range };
}).filter((f) => f.file && f.range && /noto-sans-jp-(\d+|latin)-wght-normal\.woff2$/.test(f.file));
if (faces.length < 100) throw new Error(`unexpected Noto Sans JP css (${faces.length} faces)`);

await pool(faces, 8, async (f) => {
  const size = await save(`noto-sans-jp/${f.file}`, await get(`${CDN}/npm/${NOTO}/files/${f.file}`));
  total += size; // (total += await … 로 쓰면 동시에 도는 작업끼리 합계를 덮어씀)
});

// ── 라이선스 ──
await save('licenses/Pretendard-OFL.txt', await get(`${CDN}/gh/orioncactus/pretendard@v1.3.9/LICENSE`));
await save('licenses/NotoSansJP-OFL.txt', await get(`${CDN}/npm/${NOTO}/LICENSE`));
await save('licenses/JetBrainsMono-OFL.txt', await get(`${CDN}/npm/${JETBRAINS}/LICENSE`));

// ── fonts.css ──
const css = `/* dev/fetch-fonts.mjs 가 만든 파일 — 직접 고치지 말 것.
   폰트는 모두 로컬 파일이라 인터넷 없이도 같은 모양으로 보인다. (라이선스: fonts/licenses) */

@font-face {
  font-family: 'Pretendard Variable';
  font-style: normal;
  font-weight: 45 920;
  font-display: swap;
  src: url(pretendard/PretendardVariable.woff2) format('woff2-variations');
}

@font-face {
  font-family: 'JetBrains Mono';
  font-style: normal;
  font-weight: 400;
  font-display: swap;
  src: url(jetbrains-mono/jetbrains-mono-latin-400-normal.woff2) format('woff2');
}

/* Noto Sans JP — 글자 범위별 조각. 브라우저는 화면에 나온 글자가 든 조각만 읽는다 */
${faces.map((f) => `@font-face {
  font-family: 'Noto Sans JP';
  font-style: normal;
  font-weight: 100 900;
  font-display: swap;
  src: url(noto-sans-jp/${f.file}) format('woff2-variations');
  unicode-range: ${f.range};
}`).join('\n')}
`;
await save('fonts.css', css);

console.log(`fonts: ${faces.length + 2} files, ${(total / 1e6).toFixed(1)} MB → ${out}`);
