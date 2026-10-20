// Premium American double-zero roulette wheel — procedural canvas rendering.
// 38 pockets (0, 00, 1..36); "00" is encoded as 37 and displayed as "00".
//
// The rotor and the ball are animated independently: the rotor spins up and
// coasts, while the ball is launched fast, bleeds speed against the frets,
// rattles, then drops into the winning pocket with a couple of soft bounces.
// Everything is deterministic — the ball always settles exactly on the result.

const ORDER = [
  0, 28, 9, 26, 30, 11, 7, 20, 32, 17, 5, 22, 34, 15, 3, 24, 36, 13, 1,
  37, 27, 10, 25, 29, 12, 8, 19, 31, 18, 6, 21, 33, 16, 4, 23, 35, 14, 2,
];

const RED = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);

const TAU = Math.PI * 2;
const SLOT = TAU / 38;

function labelOf(n: number): string {
  return n === 37 ? "00" : String(n);
}

function pocketColor(n: number): { top: string; bottom: string; ring: string; gloss: string } {
  if (n === 0 || n === 37) return { top: "#2ab972", bottom: "#06472a", ring: "#0f7a44", gloss: "rgba(255,255,255,0.16)" };
  if (RED.has(n)) return { top: "#e05a48", bottom: "#7c251b", ring: "#a83a2e", gloss: "rgba(255,255,255,0.12)" };
  return { top: "#3a3f49", bottom: "#101318", ring: "#1c1f26", gloss: "rgba(255,255,255,0.08)" };
}

interface SpinState {
  startTime: number;
  duration: number;
  wheelFrom: number;
  wheelTo: number;
  ballFrom: number;
  ballTo: number;
  result: number;
  wheelProgress: number[];
  ballProgress: number[];
}

export class Wheel {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private raf = 0;
  private wheelAngle = 0;
  private idleSpeed = (TAU / 60) * 0.18;
  private spin: SpinState | null = null;
  private lastFrame = 0;
  private onSettle?: (result: number) => void;
  private onTick?: () => void;
  private lastTickPocket = -1;
  private ballAngle = 0;
  private ballRadiusFactor = 0.69;
  private readonly REST_RADIUS = 0.69;
  private readonly RIM_RADIUS = 0.925;
  private lastTickTime = 0;
  private lastResult = -1;
  private settleTime = 0;
  private trail: { x: number; y: number; a: number }[] = [];

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d")!;
    this.resize();
    window.addEventListener("resize", () => this.resize());
    const parentEl = this.canvas.parentElement;
    if (parentEl) new ResizeObserver(() => this.resize()).observe(parentEl);
    this.lastFrame = performance.now();
    const loop = (t: number) => {
      const dt = Math.min((t - this.lastFrame) / 1000, 0.05);
      this.lastFrame = t;
      this.update(dt);
      this.draw();
      this.raf = requestAnimationFrame(loop);
    };
    this.raf = requestAnimationFrame(loop);
  }

  setSettleCallback(cb: (result: number) => void) {
    this.onSettle = cb;
  }

  setTickCallback(cb: () => void) {
    this.onTick = cb;
  }

  dispose() {
    cancelAnimationFrame(this.raf);
  }

  refreshSize() {
    this.resize();
  }

  get isSpinning(): boolean {
    return this.spin !== null;
  }

  private resize() {
    const parent = this.canvas.parentElement!;
    const maxByHeight = Math.max(280, window.innerHeight - 240);
    const size = Math.max(280, Math.min(parent.clientWidth, maxByHeight, 900));
    const dpr = window.devicePixelRatio || 1;
    this.canvas.style.width = `${size}px`;
    this.canvas.style.height = `${size}px`;
    this.canvas.width = size * dpr;
    this.canvas.height = size * dpr;
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  spinTo(result: number, durationMs = 24400): void {
    if (this.spin) return;
    const pocketIndex = ORDER.indexOf(result);
    // pocket-centre angle relative to the wheel
    const relEnd = pocketIndex * SLOT - Math.PI / 2;

    const wheelFrom = this.wheelAngle;
    const wheelTo = wheelFrom + (14 + Math.random() * 3) * TAU;

    // the ball is launched several full turns ahead of its landing pocket,
    // then bleeds speed and rattles its way down to relEnd.
    const relTurns = 8 + Math.random() * 2;
    const relFrom = relEnd + relTurns * TAU;

    this.trail = [];
    this.lastTickPocket = -1;
    this.lastTickTime = 0;
    this.spin = {
      startTime: performance.now(),
      duration: durationMs,
      wheelFrom,
      wheelTo,
      ballFrom: relFrom,
      ballTo: relEnd,
      result,
      wheelProgress: this.buildTable((t) => {
        if (t < 0.05) return t / 0.05;
        if (t <= 0.5) return 1;
        const u = (1 - t) / 0.5;
        return Math.pow(Math.max(0, u), 1.5);
      }),
      ballProgress: this.buildTable((t) => {
        // launch, long friction decay, slow crawl over the frets, final fall
        if (t < 0.06) return t / 0.06;
        if (t < 0.55) return 1 - (0.68 * (t - 0.06)) / 0.49;
        if (t < 0.86) return 0.32 - (0.27 * (t - 0.55)) / 0.31;
        return 0.05 * (1 - (t - 0.86) / 0.14);
      }),
    };
  }

  /** Integrates a velocity profile into a normalised progress table. */
  private buildTable(v: (t: number) => number): number[] {
    const N = 240;
    const table = new Array<number>(N + 1).fill(0);
    let acc = 0;
    const dt = 1 / N;
    for (let i = 1; i <= N; i++) {
      const t = (i - 0.5) * dt;
      acc += Math.max(0, v(t)) * dt;
      table[i] = acc;
    }
    for (let i = 0; i <= N; i++) table[i] /= acc || 1;
    return table;
  }

  private sampleProgress(table: number[], t: number): number {
    const x = Math.min(1, Math.max(0, t)) * (table.length - 1);
    const i = Math.floor(x);
    const f = x - i;
    const a = table[i];
    const b = table[Math.min(i + 1, table.length - 1)];
    return a + (b - a) * f;
  }

  private smoothstep(x: number): number {
    const c = Math.min(1, Math.max(0, x));
    return c * c * (3 - 2 * c);
  }

  private update(_dt: number): void {
    if (!this.spin) {
      this.wheelAngle += this.idleSpeed * _dt;
      return;
    }
    const s = this.spin;
    const elapsed = performance.now() - s.startTime;
    const tWheel = Math.min(1, elapsed / s.duration);
    const tBall = Math.min(1, elapsed / s.duration);

    this.wheelAngle = s.wheelFrom + (s.wheelTo - s.wheelFrom) * this.sampleProgress(s.wheelProgress, tWheel);

    if (tBall >= 1) {
      this.wheelAngle = s.wheelTo;
      this.ballAngle = s.wheelTo + s.ballTo;
      this.ballRadiusFactor = this.REST_RADIUS;
      this.lastResult = s.result;
      this.settleTime = performance.now();
      this.spin = null;
      this.onSettle?.(s.result);
      return;
    }

    const rel = s.ballFrom + (s.ballTo - s.ballFrom) * this.sampleProgress(s.ballProgress, tBall) + this.settleWobble(tBall);
    this.ballAngle = this.wheelAngle + rel;
    this.ballRadiusFactor = this.ballRadiusAt(tBall);

    // fret ticks — the ball crosses pockets; spacing naturally widens as it slows
    const nowMs = performance.now();
    const pocket = Math.floor((((this.ballAngle - this.wheelAngle) % TAU) + TAU) % TAU / SLOT);
    if (pocket !== this.lastTickPocket && nowMs - this.lastTickTime > 24) {
      this.lastTickPocket = pocket;
      this.lastTickTime = nowMs;
      this.onTick?.();
    }
  }

  /** The ball rattles across the frets with a decaying oscillation that ends
   *  at zero, so the landing stays exact. */
  private settleWobble(t: number): number {
    if (t < 0.78) return 0;
    const u = (t - 0.78) / 0.22;
    const envelope = Math.sin(Math.PI * Math.min(1, u)); // rises then falls to 0
    return SLOT * 0.30 * envelope * Math.sin(u * Math.PI * 6.5);
  }

  /** Ball height: rides the rim, drops into the pocket with a soft hop. */
  private ballRadiusAt(t: number): number {
    if (t < 0.8) return this.RIM_RADIUS;
    if (t < 0.9) {
      const u = (t - 0.8) / 0.1;
      return this.RIM_RADIUS + (this.REST_RADIUS - this.RIM_RADIUS) * this.smoothstep(u);
    }
    const u = (t - 0.9) / 0.1;
    // tiny settle hop that decays to exactly REST at t=1
    return this.REST_RADIUS + 0.02 * Math.sin(Math.min(1, u) * Math.PI) * (1 - u);
  }

  private faceCache: HTMLCanvasElement | null = null;
  private faceKey = "";

  private draw(): void {
    const ctx = this.ctx;
    const size = parseFloat(this.canvas.style.width);
    const c = size / 2;
    const R = c - 6;

    ctx.clearRect(0, 0, size, size);

    const dpr = window.devicePixelRatio || 1;
    const key = `${size}x${dpr}`;
    if (!this.faceCache || this.faceKey !== key) {
      this.faceCache = this.buildFace(size, dpr);
      this.faceKey = key;
    }

    // soft floor shadow
    ctx.beginPath();
    ctx.ellipse(c, c + R * 0.07, R * 0.99, R * 0.95, 0, 0, TAU);
    ctx.fillStyle = "rgba(0,0,0,0.5)";
    ctx.fill();

    // rotated face
    ctx.save();
    ctx.translate(c, c);
    ctx.rotate(this.wheelAngle);
    ctx.drawImage(this.faceCache, -size / 2, -size / 2, size, size);
    ctx.restore();

    // ---- fixed lighting overlay (does not rotate with the wheel) ----
    const gloss = ctx.createRadialGradient(c - R * 0.42, c - R * 0.42, R * 0.06, c, c, R * 1.02);
    gloss.addColorStop(0, "rgba(255,255,255,0.16)");
    gloss.addColorStop(0.32, "rgba(255,255,255,0.06)");
    gloss.addColorStop(0.68, "rgba(255,255,255,0)");
    gloss.addColorStop(1, "rgba(0,0,0,0.30)");
    ctx.beginPath();
    ctx.arc(c, c, R * 0.99, 0, TAU);
    ctx.fillStyle = gloss;
    ctx.fill();

    const pocketOuter = R * 0.875;
    const pocketInner = R * 0.46;

    // ---- ball ----
    let ballR: number;
    let ballA: number;
    if (this.spin) {
      ballR = R * this.ballRadiusFactor;
      ballA = this.ballAngle;
    } else {
      ballR = R * this.REST_RADIUS;
      ballA = this.wheelAngle + (this.lastResult >= 0 ? ORDER.indexOf(this.lastResult) * SLOT - Math.PI / 2 : Math.PI);
    }
    const bx = c + Math.cos(ballA) * ballR;
    const by = c + Math.sin(ballA) * ballR;
    const br = Math.max(5, R * 0.033);

    // motion streak while the ball is still travelling fast
    if (this.spin) {
      const relSpeed = this.ballSpeedAt();
      this.trail.push({ x: bx, y: by, a: Math.min(1, relSpeed * 1.4) });
      if (this.trail.length > 6) this.trail.shift();
      for (let i = 0; i < this.trail.length; i++) {
        const p = this.trail[i];
        const f = i / this.trail.length;
        ctx.beginPath();
        ctx.arc(p.x, p.y, br * (0.55 + 0.45 * f), 0, TAU);
        ctx.fillStyle = `rgba(255,255,255,${(p.a * 0.28 * f).toFixed(3)})`;
        ctx.fill();
      }
    } else {
      this.trail = [];
    }

    // ball shadow
    ctx.beginPath();
    ctx.ellipse(bx + br * 0.28, by + br * 0.4, br * 0.92, br * 0.62, 0, 0, TAU);
    ctx.fillStyle = "rgba(0,0,0,0.5)";
    ctx.fill();

    // ball body (ivory)
    const ballGrad = ctx.createRadialGradient(bx - br * 0.38, by - br * 0.42, br * 0.08, bx, by, br);
    ballGrad.addColorStop(0, "#ffffff");
    ballGrad.addColorStop(0.4, "#f6f4ee");
    ballGrad.addColorStop(0.78, "#d7d9dd");
    ballGrad.addColorStop(1, "#8e949c");
    ctx.beginPath();
    ctx.arc(bx, by, br, 0, TAU);
    ctx.fillStyle = ballGrad;
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,0.5)";
    ctx.lineWidth = 1.2;
    ctx.stroke();

    // specular
    ctx.beginPath();
    ctx.arc(bx - br * 0.34, by - br * 0.4, br * 0.2, 0, TAU);
    ctx.fillStyle = "rgba(255,255,255,0.95)";
    ctx.fill();

    // winning pocket pulse
    if (!this.spin && this.lastResult >= 0 && performance.now() - this.settleTime < 2600) {
      const idx = ORDER.indexOf(this.lastResult);
      const a0 = this.wheelAngle + idx * SLOT - SLOT / 2 - Math.PI / 2;
      const pulse = 1 + 0.08 * Math.sin((performance.now() - this.settleTime) / 120);
      ctx.beginPath();
      ctx.arc(c, c, pocketOuter * pulse, a0, a0 + SLOT);
      ctx.arc(c, c, pocketInner, a0 + SLOT, a0, true);
      ctx.closePath();
      ctx.strokeStyle = `rgba(255,235,170,${(0.9 * (1 - (performance.now() - this.settleTime) / 2600)).toFixed(3)})`;
      ctx.lineWidth = 3;
      ctx.stroke();
    }

    // result plate
    if (!this.spin && this.lastResult >= 0) {
      const plateW = R * 0.34;
      const plateH = R * 0.17;
      const px = c - plateW / 2;
      const py = c + R * 0.28;
      ctx.save();
      ctx.shadowColor = "rgba(0,0,0,0.6)";
      ctx.shadowBlur = 12;
      ctx.fillStyle = "rgba(10,12,16,0.88)";
      this.roundRect(px, py, plateW, plateH, plateH / 2);
      ctx.fill();
      ctx.restore();
      const col = this.lastResult === 0 || this.lastResult === 37 ? "#2ecc71" : RED.has(this.lastResult) ? "#e74c3c" : "#cfd4dc";
      ctx.strokeStyle = col;
      ctx.lineWidth = 2;
      this.roundRect(px, py, plateW, plateH, plateH / 2);
      ctx.stroke();
      ctx.fillStyle = col;
      ctx.font = `800 ${plateH * 0.62}px "Segoe UI", system-ui, sans-serif`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(labelOf(this.lastResult), c, py + plateH / 2 + 1);
    }
  }

  /** Normalised instantaneous ball speed (0..1) for the motion streak. */
  private ballSpeedAt(): number {
    if (!this.spin) return 0;
    const t = Math.min(1, (performance.now() - this.spin.startTime) / this.spin.duration);
    if (t < 0.06) return t / 0.06;
    if (t < 0.55) return 1 - (0.68 * (t - 0.06)) / 0.49;
    if (t < 0.86) return 0.32 - (0.27 * (t - 0.55)) / 0.31;
    return 0.05 * (1 - (t - 0.86) / 0.14);
  }

  /** Renders the static wheel face (rim, pockets, frets, numbers, hub). */
  private buildFace(size: number, dpr: number): HTMLCanvasElement {
    const cv = document.createElement("canvas");
    cv.width = Math.max(1, Math.round(size * dpr));
    cv.height = Math.max(1, Math.round(size * dpr));
    const g = cv.getContext("2d")!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const c = size / 2;
    const R = c - 6;

    // ---- outer wood rim ----
    this.ringOn(g, c, R, R * 0.99, { inner: "#24160c", outer: "#140b05" });
    this.ringOn(g, c, R * 0.99, R * 0.985, { inner: "#c9a227", outer: "#7a5a16" });
    this.ringOn(g, c, R * 0.985, R * 0.945, { inner: "#7a4f2d", outer: "#3a2413" });
    // wood sheen
    const woodSheen = g.createRadialGradient(c - R * 0.3, c - R * 0.3, R * 0.1, c, c, R);
    woodSheen.addColorStop(0, "rgba(255,235,190,0.10)");
    woodSheen.addColorStop(1, "rgba(0,0,0,0)");
    g.beginPath();
    g.arc(c, c, R * 0.985, 0, TAU);
    g.arc(c, c, R * 0.945, 0, TAU, true);
    g.fillStyle = woodSheen;
    g.fill();

    // ---- polished gold inlay ----
    const goldGrad = g.createConicGradient ? g.createConicGradient(0, c, c) : null;
    g.beginPath();
    g.arc(c, c, R * 0.94, 0, TAU);
    g.arc(c, c, R * 0.905, 0, TAU, true);
    if (goldGrad) {
      goldGrad.addColorStop(0, "#8b6a1f");
      goldGrad.addColorStop(0.12, "#f2e3ac");
      goldGrad.addColorStop(0.25, "#a9832b");
      goldGrad.addColorStop(0.5, "#fdf6dd");
      goldGrad.addColorStop(0.62, "#9a7724");
      goldGrad.addColorStop(0.82, "#e5c979");
      goldGrad.addColorStop(1, "#8b6a1f");
      g.fillStyle = goldGrad;
    } else {
      g.fillStyle = "#c9a227";
    }
    g.fill("evenodd");

    this.ringOn(g, c, R * 0.905, R * 0.885, { inner: "rgba(0,0,0,0.7)", outer: "rgba(0,0,0,0.25)" });

    // ---- pocket ring ----
    const pocketOuter = R * 0.875;
    const pocketInner = R * 0.46;
    for (let i = 0; i < 38; i++) {
      const n = ORDER[i];
      const a0 = i * SLOT - SLOT / 2 - Math.PI / 2;
      const a1 = a0 + SLOT;
      const col = pocketColor(n);

      const grad = g.createRadialGradient(c, c, pocketInner, c, c, pocketOuter);
      grad.addColorStop(0, col.top);
      grad.addColorStop(0.7, col.bottom);
      grad.addColorStop(1, col.bottom);
      g.beginPath();
      g.arc(c, c, pocketOuter, a0, a1);
      g.arc(c, c, pocketInner, a1, a0, true);
      g.closePath();
      g.fillStyle = grad;
      g.fill();

      // inner edge shading (recessed)
      g.beginPath();
      g.arc(c, c, pocketOuter, a0, a1);
      g.arc(c, c, pocketOuter - R * 0.02, a1, a0, true);
      g.closePath();
      g.fillStyle = "rgba(0,0,0,0.32)";
      g.fill();

      // top-lit highlight along the outer rim of each pocket
      g.beginPath();
      g.arc(c, c, pocketOuter - R * 0.02, a0 + SLOT * 0.1, a1 - SLOT * 0.1);
      g.arc(c, c, pocketOuter - R * 0.045, a1 - SLOT * 0.1, a0 + SLOT * 0.1, true);
      g.closePath();
      g.fillStyle = col.gloss;
      g.fill();

      // gold separators
      g.beginPath();
      g.moveTo(c + Math.cos(a0) * pocketInner, c + Math.sin(a0) * pocketInner);
      g.lineTo(c + Math.cos(a0) * pocketOuter, c + Math.sin(a0) * pocketOuter);
      g.strokeStyle = "rgba(240,217,142,0.6)";
      g.lineWidth = 1.3;
      g.stroke();

      // fret stud (the diamond the ball rattles over)
      const fa = i * SLOT - Math.PI / 2;
      const fr = R * 0.905;
      g.beginPath();
      g.arc(c + Math.cos(fa) * fr, c + Math.sin(fa) * fr, Math.max(2, R * 0.012), 0, TAU);
      g.fillStyle = "#f0d98a";
      g.fill();

      // number
      const am = a0 + SLOT / 2;
      const tr = pocketOuter - R * 0.052;
      g.save();
      g.translate(c + Math.cos(am) * tr, c + Math.sin(am) * tr);
      g.rotate(am + Math.PI / 2);
      g.fillStyle = "rgba(0,0,0,0.5)";
      g.font = `900 ${Math.max(11, R * 0.052)}px "Segoe UI", system-ui, sans-serif`;
      g.textAlign = "center";
      g.textBaseline = "middle";
      g.fillText(labelOf(n), 0.7, 0.9);
      g.fillStyle = "#fbf7ec";
      g.fillText(labelOf(n), 0, 0);
      g.restore();
    }

    // ---- inner cone + hub ----
    const cone = g.createRadialGradient(c - R * 0.16, c - R * 0.2, R * 0.02, c, c, pocketInner);
    cone.addColorStop(0, "#343943");
    cone.addColorStop(0.55, "#16191f");
    cone.addColorStop(1, "#090b0e");
    g.beginPath();
    g.arc(c, c, pocketInner, 0, TAU);
    g.fillStyle = cone;
    g.fill();

    for (let i = 0; i < 8; i++) {
      const a = (i * TAU) / 8;
      const grad = g.createLinearGradient(
        c + Math.cos(a) * R * 0.12, c + Math.sin(a) * R * 0.12,
        c + Math.cos(a) * R * 0.44, c + Math.sin(a) * R * 0.44,
      );
      grad.addColorStop(0, "rgba(240,217,142,0.55)");
      grad.addColorStop(1, "rgba(240,217,142,0.06)");
      g.beginPath();
      g.moveTo(c + Math.cos(a) * R * 0.075, c + Math.sin(a) * R * 0.075);
      g.lineTo(c + Math.cos(a) * R * 0.44, c + Math.sin(a) * R * 0.44);
      g.strokeStyle = grad;
      g.lineWidth = Math.max(2, R * 0.008);
      g.stroke();
    }

    this.ringOn(g, c, R * 0.17, R * 0.145, { inner: "#f6e6ad", outer: "#9a7724" });
    this.ringOn(g, c, R * 0.14, R * 0.105, { inner: "#4a3a16", outer: "#120c04" });
    // dome highlight
    const hub = g.createRadialGradient(c - R * 0.035, c - R * 0.04, R * 0.005, c, c, R * 0.14);
    hub.addColorStop(0, "rgba(255,244,205,0.9)");
    hub.addColorStop(0.4, "rgba(255,244,205,0.15)");
    hub.addColorStop(1, "rgba(0,0,0,0)");
    g.beginPath();
    g.arc(c, c, R * 0.14, 0, TAU);
    g.fillStyle = hub;
    g.fill();

    g.fillStyle = "#e8cf86";
    g.font = `700 ${Math.max(9, R * 0.04)}px "Segoe UI", system-ui, sans-serif`;
    g.textAlign = "center";
    g.textBaseline = "middle";
    g.fillText("PR", c, c + 1);

    return cv;
  }

  private ringOn(g: CanvasRenderingContext2D, c: number, rOuter: number, rInner: number, colors: { inner: string; outer: string }): void {
    const grad = g.createRadialGradient(c, c, rInner, c, c, rOuter);
    grad.addColorStop(0, colors.inner);
    grad.addColorStop(1, colors.outer);
    g.beginPath();
    g.arc(c, c, rOuter, 0, TAU);
    g.arc(c, c, rInner, 0, TAU, true);
    g.closePath();
    g.fillStyle = grad;
    g.fill();
  }

  private roundRect(x: number, y: number, w: number, h: number, r: number): void {
    const ctx = this.ctx;
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }
}
