// 연출 길이 상수를 실측에서 되뽑는다.
//
//   node tools/fit-timing.js [--asym N] [--loo] [--profile] <LogOutput.log ...>
//
// ── 어디서 오는 등식인가 ────────────────────────────────────────────────────
// 동기화 신호(결정 창 열림)는 게임이 보낸다. 사용자가 아니라 게임이 여는 것이라, 그 시각은
// 앞선 연출 사슬이 끝난 정확한 시점이다. 화면도 연출을 차례대로 재생하므로
//
//     끝_i = max(도착_i, 끝_{i-1}) + 길이_i,   끝_마지막 = 동기화 시각
//
// 이 성립한다. 이 max를 접어서 "합 = 경과 - 유휴"로 만들면 안 된다 — 로그의 유휴는 모델이
// 자기 (맞추려는) 길이로 계산한 값이라 화면이 실제로 쉰 구간과 다르다.
//
// ── 왜 비대칭 손실인가 ──────────────────────────────────────────────────────
// 잔차가 양수면 모델이 화면보다 늦게 끝난다고 본 것이다 → 로그가 늦게 뜬다. 불편할 뿐이다.
// 잔차가 음수면 화면이 아직 그 장면에 없는데 로그를 내보낸 것이다 → **스포일러**. PK 결과가
// 타격 연출 전에 뜨는 식이라 게임이 망가진다.
//
// 대칭 제곱오차로 맞추면 이 둘을 같은 값으로 취급한다. 실제 목적이 비대칭이므로 손실도
// 비대칭이어야 한다. 상수가 식별되지 않아 평탄한 구간이 넓을 때(프로파일 손실 참고),
// 비대칭 손실은 그 구간의 안전한 쪽 끝을 고른다.
//
// 필요한 로그: Diagnostics.TraceTiming = true 로 남긴 segitem / synclead 줄.

const fs = require('fs');
const path = require('path');

const KEYS = ['card', 'submit', 'attack', 'skill', 'step', 'dicefix', 'start', 'effect', 'turn', 'land'];

/// 무작위 시작점의 범위. 값의 상한이 아니라 탐색 폭이다.
const RANGE = {
  card: 8000, submit: 8000, attack: 15000, skill: 8000, step: 800,
  dicefix: 4000, start: 25000, effect: 4000, turn: 3000, land: 12000,
};

/// UI/OverlaySchedule.cs 의 현재 상수 (밀리초).
const CURRENT = {
  card: 2870, submit: 2230, attack: 9450, skill: 1920, step: 300,
  dicefix: 2700, start: 18150, effect: 0, turn: 0, land: 0,
};

const ONE = Object.fromEntries(KEYS.map(k => [k, 1]));
const RESTARTS = 250;

/// 벽시계와 프레임 시계가 이만큼 벌어진 구간은 버린다. 절전·긴 멈춤에서 생긴다.
const STALL_MS = 1500;

const args = process.argv.slice(2);
const flag = (name, fallback) => {
  const i = args.indexOf(name);
  if (i < 0) return fallback;
  const next = args[i + 1];
  if (next === undefined || next.startsWith('--')) { args.splice(i, 1); return true; }
  args.splice(i, 2);
  return next;
};
const ASYM = parseFloat(flag('--asym', '4'));
/// 이 이상 늦어지면 벌점이 급격히 커진다.
const CAP = parseFloat(flag('--cap', '6000'));
const WANT_LOO = flag('--loo', false);
const WANT_PROFILE = flag('--profile', false);
const files = args;

if (files.length === 0) {
  console.error('사용법: node tools/fit-timing.js [--asym N] [--loo] [--profile] <LogOutput.log ...>');
  process.exit(1);
}

const wallMs = t => {
  const [h, m, s] = t.split(':');
  return (+h * 3600 + +m * 60 + parseFloat(s)) * 1000;
};

function parse(file, tag) {
  const segs = [];
  let items = [];
  let firstWall = null;
  for (const raw of fs.readFileSync(file, 'utf8').split(/\r?\n/)) {
    const m = raw.match(/\[timing\] ([\d:.]+) (segitem|synclead) (.*)$/);
    if (!m) continue;
    const kv = {};
    for (const p of m[3].trim().split(/\s+/)) {
      const [k, v] = p.split('=');
      kv[k] = v;
    }
    const wall = wallMs(m[1]);
    if (m[2] === 'segitem') {
      if (firstWall === null) firstWall = wall - parseInt(kv.at);
      items.push({ kind: kv.kind, units: +kv.units, at: parseInt(kv.at), speed: parseFloat(kv.speed) });
    } else {
      const elapsed = parseInt(kv.elapsed);
      // 프레임 시계 경과와 벽시계 경과의 차이. 게임이 멈춰 있었으면 벌어진다.
      const drift = firstWall === null ? 0 : Math.abs((wall - firstWall) - elapsed);
      // 신호마다 의미가 다르다. 검증된 "결정 창 열림"(TimeWastingS2C 5308, 본문 14바이트)만
      // 연출 사슬의 끝을 뜻한다. 카드 제출·PK 선택 응답(5036/5040)은 단계 중간에 오므로
      // 커서를 맞추는 데는 써도 상수를 배우는 관측으로는 쓰면 안 된다.
      const cmd = kv.cmd === undefined ? null : +kv.cmd;
      const len = kv.len === undefined ? null : +kv.len;
      const learnable = cmd === null ? null : (cmd === 5308 && len >= 14);
      segs.push({ tag, elapsed, items, drift, cmd, len, learnable });
      items = [];
      firstWall = null;
    }
  }
  // 1라운드 페이지에만 판 시작 연출이 붙는다. 로그에 라운드 번호가 없으므로 처음 하나만 본다.
  let seen = false;
  for (const s of segs) for (const it of s.items) {
    if (it.kind !== 'round') continue;
    it.isStart = !seen;
    seen = true;
  }
  return segs;
}

function duration(it, p) {
  switch (it.kind) {
    case 'card': return p.card;
    case 'submit': return p.submit;
    case 'attack': return p.attack;
    case 'skill': return p.skill;
    case 'dice': return p.dicefix;   // 칸 수에 비례하지 않는다 (R²=0.19)
    case 'move': return it.units * p.step;   // 추가 이동에는 주사위 연출이 없다
    case 'effect': return p.effect;
    case 'turn': return p.turn;
    case 'land': return p.land;
    case 'round': return it.isStart ? p.start : 0;
    default: return 0;
  }
}

const simulate = (seg, p) =>
  seg.items.reduce((end, it) => Math.max(it.at, end) + duration(it, p) / (it.speed || 1), 0);
const residuals = (segs, p) => segs.map(s => simulate(s, p) - s.elapsed);

/// 조기 표시(음수)에 ASYM배 가중을 주되, 늦음에도 벌점을 두고 CAP을 넘으면 급격히 키운다.
/// 한쪽만 벌하면 해가 "무조건 더 늦게"로 달아난다 — 계수를 1~16으로 훑었더니 최악 지연이
/// 5.3초에서 15.7초까지 단조 증가했다.
const loss = (segs, p) => residuals(segs, p).reduce((a, r) => {
  if (r < 0) return a + (ASYM * r) * (ASYM * r);
  const over = Math.max(0, r - CAP);
  return a + r * r + (4 * over) * (4 * over);
}, 0);
const rms = (segs, p) => Math.sqrt(residuals(segs, p).reduce((a, r) => a + r * r, 0) / segs.length);

/// 좌표 하강. max() 때문에 미분이 안 되므로 격자를 반씩 줄이며 훑는다.
function fit(segs, init, objective, frozen = {}) {
  let p = { ...init, ...frozen };
  const free = KEYS.filter(k => !(k in frozen));
  for (let size = 4000; size >= 1;) {
    let improved = false;
    for (const k of free) {
      const base = objective(segs, p);
      for (const d of [size, -size]) {
        const q = { ...p, [k]: Math.max(0, p[k] + d) };
        if (objective(segs, q) < base - 1e-9) { p = q; improved = true; break; }
      }
    }
    if (!improved) size /= 2;
  }
  return p;
}

function best(segs, objective, restarts = RESTARTS, frozen = {}) {
  let b = null;
  for (let i = 0; i < restarts; i++) {
    const init = i === 0 ? CURRENT : Object.fromEntries(KEYS.map(k => [k, Math.random() * RANGE[k]]));
    const p = fit(segs, init, objective, frozen);
    const c = objective(segs, p);
    if (!b || c < b.c) b = { p, c };
  }
  return b.p;
}

/// 실제 목적으로 본 성적. RMS보다 이쪽이 중요하다.
function score(segs, p) {
  const r = residuals(segs, p);
  const early = r.filter(x => x < 0);
  const late = r.filter(x => x >= 0);
  const avg = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : 0;
  return {
    rms: rms(segs, p),
    // 이것은 구간 "종료" 잔차이지 개별 로그의 조기 표시가 아니다. 개별 줄이 화면보다
    // 먼저 떴는지는 F10 mark 관측으로만 잴 수 있다 — 여기 수치는 그 대리 지표다.
    // 개수보다 크기가 중요해 1초를 넘는 것만 센다.
    bad: early.filter(x => x < -1000).length,
    worstEarly: early.length ? Math.min(...early) : 0,
    avgEarly: avg(early),
    avgLate: avg(late),
    worstLate: late.length ? Math.max(...late) : 0,
  };
}

let all = [];
for (const f of files) all = all.concat(parse(f, path.basename(f).replace(/[-.].*/, '')));

const stalled = all.filter(s => s.drift > STALL_MS).length;
all = all.filter(s => s.drift <= STALL_MS);

// 동기화와 같은 시각(또는 그 뒤)에 도착한 항목은 아직 화면에 안 나왔다. 넣으면 "합 = 0"
// 같은 축퇴식이 되어 상수를 끌고 간다.
for (const s of all) s.items = s.items.filter(it => it.at < s.elapsed);
let usable = all.filter(s => s.items.some(it => duration(it, ONE) > 0));

// 학습 등급 신호만 남긴다. cmd 를 안 남기던 빌드의 로그는 null 이라 그대로 쓴다.
const graded = usable.filter(s => s.learnable !== null);
const dropped = graded.filter(s => !s.learnable).length;
if (graded.length) usable = usable.filter(s => s.learnable !== false);

const tags = [...new Set(usable.map(s => s.tag))];
console.log(`식 ${usable.length}개 / 판 ${tags.length}개 (${tags.join(', ')})`
            + (stalled ? `  · 멈춤으로 버린 구간 ${stalled}개` : '')
            + (dropped ? `  · 학습등급 아닌 신호로 버린 구간 ${dropped}개` : ''));
console.log(`비대칭 계수 ${ASYM} (음수 잔차 = 스포일러에 ${ASYM}배 가중)\n`);

const symmetric = best(usable, (s, p) => residuals(s, p).reduce((a, r) => a + r * r, 0));
const asymmetric = best(usable, loss);

const sets = [['현재값', CURRENT], ['대칭 최적', symmetric], ['비대칭 최적', asymmetric]];
console.log('               RMS   1초↑이른종료   최악 이른   평균 늦음   최악 늦음');
for (const [label, p] of sets) {
  const s = score(usable, p);
  console.log(`  ${label.padEnd(11)}${String(Math.round(s.rms)).padStart(5)}ms  `
    + `${String(s.bad).padStart(6)}/${usable.length}  `
    + `${String(Math.round(s.worstEarly)).padStart(9)}ms  `
    + `${String(Math.round(s.avgLate)).padStart(8)}ms  `
    + `${String(Math.round(s.worstLate)).padStart(8)}ms`);
}

console.log('\n상수');
console.log('            ' + KEYS.map(k => k.slice(0, 7).padStart(8)).join(''));
for (const [label, p] of sets) {
  console.log(`  ${label.padEnd(10)}` + KEYS.map(k => String(Math.round(p[k])).padStart(8)).join(''));
}

if (WANT_PROFILE) {
  // 상수를 고정하고 나머지를 다시 최적화한다. 곡선이 평탄하면 데이터가 그 상수에 대해
  // 아무 말도 안 하는 것이다. "최적 부근 해들의 범위"는 신뢰구간이 아니므로 쓰지 않는다.
  console.log('\n── 프로파일 손실 (고정 후 나머지 재최적화) ──');
  for (const k of KEYS) {
    const hi = RANGE[k];
    const pts = [];
    for (let i = 0; i <= 10; i++) {
      const v = hi * i / 10;
      pts.push({ v, rms: rms(usable, best(usable, loss, 40, { [k]: v })) });
    }
    const floor = Math.min(...pts.map(x => x.rms));
    const flat = pts.filter(x => x.rms < floor * 1.1).map(x => x.v);
    const span = Math.max(...flat) - Math.min(...flat);
    const verdict = span < hi * 0.25 ? '식별됨' : '평탄 — 데이터가 말하지 않음';
    console.log(`  ${k.padEnd(8)} 10%이내 ${Math.round(Math.min(...flat))}~${Math.round(Math.max(...flat))}ms  ${verdict}`);
  }
}

if (tags.length > 1) {
  // 판 하나를 통째로 빼고 맞춘 뒤 그 판에서 평가한다. 상수를 맞춘 데이터에서의 성적은
  // 아무것도 말해주지 않는다. 3판으로 통계적 보장을 말할 수는 없지만, 현재값보다
  // 나아졌는지는 이걸로만 판단할 수 있다.
  console.log('\n── 뺀 판에서의 정책 평가 (그 판은 적합에 안 씀) ──');
  console.log('  판        정책        1초↑이른종료   최악 이른   평균 늦음   최악 늦음');
  for (const held of tags) {
    const train = usable.filter(s => s.tag !== held);
    const test = usable.filter(s => s.tag === held);
    const cands = [
      ['현재값', CURRENT],
      ['대칭', best(train, (s, q) => residuals(s, q).reduce((a, r) => a + r * r, 0), 120)],
      [`비대칭${ASYM}/${CAP / 1000}s`, best(train, loss, 120)],
    ];
    for (const [label, p] of cands) {
      const s = score(test, p);
      console.log(`  ${held.padEnd(9)} ${label.padEnd(12)}${String(s.bad).padStart(6)}/${test.length}  `
        + `${String(Math.round(s.worstEarly)).padStart(9)}ms  ${String(Math.round(s.avgLate)).padStart(8)}ms  `
        + `${String(Math.round(s.worstLate)).padStart(8)}ms`);
    }
  }
}

if (WANT_LOO && tags.length > 1) {
  console.log('\n── 판 하나씩 빼고 다시 적합 ──');
  const cols = ['land', 'attack', 'start', 'card', 'effect', 'step'];
  console.log('  뺀판        식   RMS   ' + cols.map(c => c.padStart(7)).join(''));
  for (const drop of [null, ...tags]) {
    const sub = drop ? usable.filter(s => s.tag !== drop) : usable;
    const p = best(sub, loss, 120);
    console.log(`  ${(drop || '(전체)').padEnd(11)}${String(sub.length).padStart(2)} ${String(Math.round(rms(sub, p))).padStart(5)}ms `
      + cols.map(c => String(Math.round(p[c])).padStart(7)).join(''));
  }
}

console.log('\n구간별 잔차 (음수 = 스포일러)');
const ra = residuals(usable, asymmetric);
const rc = residuals(usable, CURRENT);
usable.forEach((s, i) => {
  const n = {};
  for (const it of s.items) {
    if (it.kind === 'round' && !it.isStart) continue;
    const k = it.kind === 'dice' || it.kind === 'move' ? 'step' : it.kind === 'round' ? 'start' : it.kind;
    if (!KEYS.includes(k)) continue;
    n[k] = (n[k] || 0) + (k === 'step' ? it.units : 1);
  }
  const comp = Object.entries(n).map(([k, v]) => `${k}${v}`).join(' ');
  const mark = ra[i] < 0 ? ' ←' : '';
  console.log(`  ${s.tag} 경과=${String(s.elapsed).padStart(6)}ms  현재=${String(Math.round(rc[i])).padStart(7)}ms`
              + `  비대칭=${String(Math.round(ra[i])).padStart(6)}ms   [${comp}]${mark}`);
});
