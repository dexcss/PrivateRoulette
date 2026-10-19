// Premium American double-zero roulette wheel Ã¢â‚¬â€ procedural canvas rendering.
// 38 pockets (0, 00, 1..36); "00" is encoded as 37 and displayed as "00".
// Everything here is drawn by us: layered gradients, gold inlay, ball shadow
// and trail, rim sheen, and a physically-flavoured spin.

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

function pocketColor(n: number): { top: string; bottom: string; ring: string } {
  if (n === 0 || n === 37) return { top: "#1aa05e", bottom: "#0a5c33", ring: "#0f7a44" };
  if (RED.has(n)) return { top: "#d24b3c", bottom: "#8e2f24", ring: "#a83a2e" };
  return { top: "#2a2d35", bottom: "#14161b", ring: "#1c1f26" };
}

interface SpinState {
  startTime: number;
  duration: number;
  preRoll: number;
  wheelFrom: number;
  wheelTo: number;
  ballFrom: number;
  ballTo: number;
  result: number;
  progress: number[];
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
  private lastTickTime = 0;
  private lastResult = -1;
  private settleTime = 0;
  private trail: { x: number; y: number }[] = [];

  constructor(canvas: HTMLCanvasElement) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d")!;
    this.resize();
    window.addEventListener("resize", () => this.resize());
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

  get isSpinning(): boolean {
    return this.spin !== null;
  }

  private resize() {
    const parent = this.canvas.parentElement!;
    const size = Math.max(280, Math.min(parent.clientWidth, 620));
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
    // relative angle of the pocket centre (ball angle minus wheel angle)
    const relEnd = pocketIndex * SLOT - Math.PI / 2;

    const wheelFrom = this.wheelAngle;
    const wheelTo = wheelFrom + (1.6 + Math.random() * 0.5) * TAU;

    // animate the ball RELATIVE to the wheel so it always settles exactly in
    // the winning pocket; the ball races against the wheel, then bleeds speed
    // linearly over a long tail for a suspenseful approach.
    const relTurns = 9 + Math.random() * 2;
    const relFrom = relEnd + relTurns * TAU;

    this.trail = [];
    this.spin = {
      startTime: performance.now(),
      duration: durationMs,
      preRoll: 2400,
      wheelFrom,
      wheelTo,
      ballFrom: relFrom,
      ballTo: relEnd,
      result,
      progress: this.buildProgressTable(),
    };
  }

  /** Velocity profile: quick ramp-up, cruise, then a linear decay to zero.
   *  Integrated once per spin; guarantees a long, readable landing. */
  private buildProgressTable(): number[] {
    const N = 240;
    const v = (t: number): number => {
      if (t < 0.05) return t / 0.05;              // launch
      if (t <= 0.5) return 1;                     // cruise
      const u = (1 - t) / 0.5;                    // brake progress
      return Math.pow(Math.max(0, u), 1.5);       // convex brake: long crawl
    };
    const table = new Array<number>(N + 1).fill(0);
    let acc = 0;
    const dt = 1 / N;
    for (let i = 1; i <= N; i++) {
      const t = (i - 0.5) * dt;
      acc += v(t) * dt;
      table[i] = acc;
    }
    for (let i = 0; i <= N; i++) table[i] /= acc;
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

  private easeOutCubic(x: number): number {
    return 1 - Math.pow(1 - x, 3);
  }

  private smoothstep(x: number): number {
    const c = Math.min(1, Math.max(0, x));
    return c * c * (3 - 2 * c);
  }

  private update(dt: number): void {
    if (!this.spin) {
      this.wheelAngle += this.idleSpeed * dt;
      return;
    }
    const s = this.spin;
    const elapsed = performance.now() - s.startTime;
    const tWheel = Math.min(1, elapsed / s.duration);

    // the rotor spins up then bleeds off speed over the whole sequence
    this.wheelAngle = s.wheelFrom + (s.wheelTo - s.wheelFrom) * this.smoothstep(tWheel);

    // pre-roll: the ball rides the rim while the wheel gets going
    const tBall = Math.max(0, (elapsed - s.preRoll) / (s.duration - s.preRoll));
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

    // ball is animated relative to the wheel: guaranteed to settle on ballTo
    const rel = s.ballFrom + (s.ballTo - s.ballFrom) * this.sampleProgress(s.progress, tBall) + this.settleWobble(tBall);
    this.ballAngle = this.wheelAngle + rel;
    this.ballRadiusFactor = this.ballRadiusAt(tBall);

    // pocket-crossing ticks (throttled so fast spins don't buzz)
    const nowMs = performance.now();
    const pocket = Math.floor((((this.ballAngle - this.wheelAngle) % TAU) + TAU) % TAU / SLOT);
    if (pocket !== this.lastTickPocket && nowMs - this.lastTickTime > 26) {
      this.lastTickPocket = pocket;
      this.lastTickTime = nowMs;
      this.onTick?.();
    }
  }

  /** One gentle overshoot as the ball slows into the pocket.
   *  Starts and ends at zero so the landing is exact. */
  private settleWobble(t: number): number {
    if (t < 0.9) return 0;
    const p = (t - 0.9) / 0.1;
    return Math.sin(p * Math.PI * 1.5) * SLOT * 0.16 * Math.pow(1 - p, 3);
  }

  /** Ball height: rides the rim, drops smoothly into the well with one soft hop.
   *  Continuous at every boundary â€” no popping. */
  private ballRadiusAt(t: number): number {
    const dropStart = 0.62;
    const dropEnd = 0.78;

    if (t < dropStart) return 0.925;
    if (t < dropEnd) {
      const u = (t - dropStart) / (dropEnd - dropStart);
      return 0.925 + (this.REST_RADIUS - 0.925) * this.smoothstep(u);
    }
    const p = (t - dropEnd) / 0.22;
    return this.REST_RADIUS + Math.sin(Math.min(1, p) * Math.PI) * 0.02 * Math.pow(1 - p, 2);
  }

  private faceCache: HTMLCanvasElement | null = null;
  private faceKey = "";

  private draw(): void {
    const ctx = this.ctx;
    const size = parseFloat(this.canvas.style.width);
    const c = size / 2;
    const R = c - 6;

    ctx.clearRect(0, 0, size, size);

    // rebuild the static face only when the size changes
    const dpr = window.devicePixelRatio || 1;
    const key = `${size}x${dpr}`;
    if (!this.faceCache || this.faceKey !== key) {
      this.faceCache = this.buildFace(size, dpr);
      this.faceKey = key;
    }

    // soft shadow under the wheel (cheap, no canvas filters)
    ctx.beginPath();
    ctx.ellipse(c, c + R * 0.06, R * 0.99, R * 0.97, 0, 0, TAU);
    ctx.fillStyle = "rgba(0,0,0,0.45)";
    ctx.fill();

    // one rotated bitmap instead of ~40 gradients + 76 text draws per frame
    ctx.save();
    ctx.translate(c, c);
    ctx.rotate(this.wheelAngle);
    ctx.drawImage(this.faceCache, -size / 2, -size / 2, size, size);
    ctx.restore();

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
    const br = Math.max(5, R * 0.034);

    // trail while spinning fast
    if (this.spin) {
      this.trail.push({ x: bx, y: by });
      if (this.trail.length > 7) this.trail.shift();
      for (let i = 0; i < this.trail.length; i++) {
        const p = this.trail[i];
        const a = (i / this.trail.length) * 0.22;
        ctx.beginPath();
        ctx.arc(p.x, p.y, br * (0.6 + 0.4 * (i / this.trail.length)), 0, TAU);
        ctx.fillStyle = `rgba(255,255,255,${a})`;
        ctx.fill();
      }
    } else {
      this.trail = [];
    }

    // ball shadow
    ctx.beginPath();
    ctx.ellipse(bx + br * 0.25, by + br * 0.35, br * 0.95, br * 0.7, 0, 0, TAU);
    ctx.fillStyle = "rgba(0,0,0,0.45)";
    ctx.fill();

    // ball body
    const ballGrad = ctx.createRadialGradient(bx - br * 0.38, by - br * 0.42, br * 0.1, bx, by, br);
    ballGrad.addColorStop(0, "#ffffff");
    ballGrad.addColorStop(0.45, "#f7f7f5");
    ballGrad.addColorStop(0.8, "#d9dbdf");
    ballGrad.addColorStop(1, "#9aa0a8");
    ctx.beginPath();
    ctx.arc(bx, by, br, 0, TAU);
    ctx.fillStyle = ballGrad;
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,0.45)";
    ctx.lineWidth = 1.2;
    ctx.stroke();

    // specular
    ctx.beginPath();
    ctx.arc(bx - br * 0.34, by - br * 0.4, br * 0.18, 0, TAU);
    ctx.fillStyle = "rgba(255,255,255,0.95)";
    ctx.fill();

    // ---- winning pocket pulse after settle ----
    if (!this.spin && this.lastResult >= 0 && performance.now() - this.settleTime < 2600) {
      const idx = ORDER.indexOf(this.lastResult);
      const a0 = this.wheelAngle + idx * SLOT - SLOT / 2 - Math.PI / 2;
      const pulse = 1 + 0.08 * Math.sin((performance.now() - this.settleTime) / 120);
      ctx.beginPath();
      ctx.arc(c, c, pocketOuter * pulse, a0, a0 + SLOT);
      ctx.arc(c, c, pocketInner, a0 + SLOT, a0, true);
      ctx.closePath();
      ctx.strokeStyle = `rgba(255,235,170,${0.85 * (1 - (performance.now() - this.settleTime) / 2600)})`;
      ctx.lineWidth = 3;
      ctx.stroke();
    }

    // ---- result plate ----
    if (!this.spin && this.lastResult >= 0) {
      const plateW = R * 0.34;
      const plateH = R * 0.17;
      const px = c - plateW / 2;
      const py = c + R * 0.28;
      ctx.save();
      ctx.shadowColor = "rgba(0,0,0,0.6)";
      ctx.shadowBlur = 10;
      ctx.fillStyle = "rgba(10,12,16,0.85)";
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

  /** Renders the static wheel face (rim, pockets, numbers, hub) once per size. */
  private buildFace(size: number, dpr: number): HTMLCanvasElement {
    const cv = document.createElement("canvas");
    cv.width = Math.max(1, Math.round(size * dpr));
    cv.height = Math.max(1, Math.round(size * dpr));
    const g = cv.getContext("2d")!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const c = size / 2;
    const R = c - 6;

    // ambient halo
    const halo = g.createRadialGradient(c, c, R * 0.6, c, c, R * 1.05);
    halo.addColorStop(0, "rgba(201,162,39,0)");
    halo.addColorStop(0.8, "rgba(201,162,39,0.10)");
    halo.addColorStop(1, "rgba(201,162,39,0)");
    g.fillStyle = halo;
    g.beginPath();
    g.arc(c, c, R * 1.03, 0, TAU);
    g.fill();

    // ---- outer rim: layered wood + gold inlay ----
    this.ringOn(g, c, R, R * 0.985, { inner: "#4a2f1b", outer: "#24160c" });
    this.ringOn(g, c, R * 0.972, R * 0.94, { inner: "#7a4f2d", outer: "#50331c" });

    // polished gold inlay (fixed sheen)
    const goldGrad = g.createConicGradient ? g.createConicGradient(0, c, c) : null;
    g.beginPath();
    g.arc(c, c, R * 0.935, 0, TAU);
    g.arc(c, c, R * 0.905, 0, TAU, true);
    if (goldGrad) {
      goldGrad.addColorStop(0, "#8b6a1f");
      goldGrad.addColorStop(0.12, "#e8cf86");
      goldGrad.addColorStop(0.25, "#a9832b");
      goldGrad.addColorStop(0.5, "#f2e3ac");
      goldGrad.addColorStop(0.62, "#9a7724");
      goldGrad.addColorStop(0.82, "#e5c979");
      goldGrad.addColorStop(1, "#8b6a1f");
      g.fillStyle = goldGrad;
    } else {
      g.fillStyle = "#c9a227";
    }
    g.fill("evenodd");

    // inner rim shadow
    this.ringOn(g, c, R * 0.905, R * 0.885, { inner: "rgba(0,0,0,0.65)", outer: "rgba(0,0,0,0.25)" });

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
      grad.addColorStop(1, col.bottom);
      g.beginPath();
      g.arc(c, c, pocketOuter, a0, a1);
      g.arc(c, c, pocketInner, a1, a0, true);
      g.closePath();
      g.fillStyle = grad;
      g.fill();

      // inner edge shading (depth)
      g.beginPath();
      g.arc(c, c, pocketOuter, a0, a1);
      g.arc(c, c, pocketOuter - R * 0.018, a1, a0, true);
      g.closePath();
      g.fillStyle = "rgba(0,0,0,0.28)";
      g.fill();

      // gold separators
      g.beginPath();
      g.moveTo(c + Math.cos(a0) * pocketInner, c + Math.sin(a0) * pocketInner);
      g.lineTo(c + Math.cos(a0) * pocketOuter, c + Math.sin(a0) * pocketOuter);
      g.strokeStyle = "rgba(232,207,134,0.55)";
      g.lineWidth = 1.2;
      g.stroke();

      // number plate
      const am = a0 + SLOT / 2;
      const tr = pocketOuter - R * 0.052;
      g.save();
      g.translate(c + Math.cos(am) * tr, c + Math.sin(am) * tr);
      g.rotate(am + Math.PI / 2);
      g.fillStyle = "rgba(0,0,0,0.45)";
      g.font = `800 ${Math.max(10, R * 0.052)}px "Segoe UI", system-ui, sans-serif`;
      g.textAlign = "center";
      g.textBaseline = "middle";
      g.fillText(labelOf(n), 0.6, 0.8);
      g.fillStyle = "#f6efdd";
      g.fillText(labelOf(n), 0, 0);
      g.restore();
    }

    // ---- inner cone ----
    const cone = g.createRadialGradient(c - R * 0.18, c - R * 0.22, R * 0.02, c, c, pocketInner);
    cone.addColorStop(0, "#2f333c");
    cone.addColorStop(0.55, "#171a20");
    cone.addColorStop(1, "#0a0c10");
    g.beginPath();
    g.arc(c, c, pocketInner, 0, TAU);
    g.fillStyle = cone;
    g.fill();

    // spokes (fixed in wheel space)
    for (let i = 0; i < 8; i++) {
      const a = (i * TAU) / 8;
      const grad = g.createLinearGradient(
        c + Math.cos(a) * R * 0.12, c + Math.sin(a) * R * 0.12,
        c + Math.cos(a) * R * 0.44, c + Math.sin(a) * R * 0.44,
      );
      grad.addColorStop(0, "rgba(232,207,134,0.55)");
      grad.addColorStop(1, "rgba(232,207,134,0.08)");
      g.beginPath();
      g.moveTo(c + Math.cos(a) * R * 0.075, c + Math.sin(a) * R * 0.075);
      g.lineTo(c + Math.cos(a) * R * 0.44, c + Math.sin(a) * R * 0.44);
      g.strokeStyle = grad;
      g.lineWidth = Math.max(2, R * 0.008);
      g.stroke();
    }

    // hub rings + monogram
    this.ringOn(g, c, R * 0.17, R * 0.145, { inner: "#f0d98a", outer: "#a9832b" });
    this.ringOn(g, c, R * 0.135, R * 0.11, { inner: "#3b2f12", outer: "#17130a" });
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
