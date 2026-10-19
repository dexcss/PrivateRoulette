// Premium American double-zero roulette wheel — procedural canvas rendering.
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
  wheelFrom: number;
  wheelTo: number;
  ballFrom: number;
  ballTo: number;
  result: number;
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
      this.draw(t / 1000);
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

  spinTo(result: number, durationMs = 8600): void {
    if (this.spin) return;
    const pocketIndex = ORDER.indexOf(result);
    // relative angle of the pocket centre (ball angle minus wheel angle)
    const relEnd = pocketIndex * SLOT - Math.PI / 2;

    const wheelFrom = this.wheelAngle;
    const wheelTo = wheelFrom + (4.5 + Math.random() * 1.5) * TAU;

    // animate the ball RELATIVE to the wheel so it always settles exactly in
    // the winning pocket; the ball races against the wheel, decelerates,
    // drops inward and rattles into place.
    const relTurns = 11 + Math.random() * 3;
    const relFrom = relEnd + relTurns * TAU;

    this.trail = [];
    this.spin = {
      startTime: performance.now(),
      duration: durationMs,
      wheelFrom,
      wheelTo,
      ballFrom: relFrom,
      ballTo: relEnd,
      result,
    };
  }

  private easeOutCubic(x: number): number {
    return 1 - Math.pow(1 - x, 3);
  }

  private update(dt: number): void {
    if (!this.spin) {
      this.wheelAngle += this.idleSpeed * dt;
      return;
    }
    const s = this.spin;
    const t = (performance.now() - s.startTime) / s.duration;
    if (t >= 1) {
      this.wheelAngle = s.wheelTo;
      this.ballAngle = s.wheelTo + s.ballTo;
      this.ballRadiusFactor = this.REST_RADIUS;
      this.lastResult = s.result;
      this.settleTime = performance.now();
      this.spin = null;
      this.onSettle?.(s.result);
      return;
    }

    // wheel keeps turning through the whole spin
    this.wheelAngle = s.wheelFrom + (s.wheelTo - s.wheelFrom) * this.easeOutCubic(t);

    // ball is animated relative to the wheel: guaranteed to settle on relEnd
    const rel = s.ballFrom + (s.ballTo - s.ballFrom) * this.easeOutQuint(t) + this.rattle(t);
    this.ballAngle = this.wheelAngle + rel;
    this.ballRadiusFactor = this.ballRadiusAt(t);

    // pocket-crossing ticks (throttled so fast spins don't buzz)
    const nowMs = performance.now();
    const pocket = Math.floor((((this.ballAngle - this.wheelAngle) % TAU) + TAU) % TAU / SLOT);
    if (pocket !== this.lastTickPocket && nowMs - this.lastTickTime > 26) {
      this.lastTickPocket = pocket;
      this.lastTickTime = nowMs;
      this.onTick?.();
    }
  }

  private easeOutQuint(x: number): number {
    return 1 - Math.pow(1 - x, 5);
  }

  /** damped rattle as the ball skips across pocket separators */
  private rattle(t: number): number {
    if (t < 0.82) return 0;
    const p = (t - 0.82) / 0.18;
    return Math.sin(p * Math.PI * 6.5) * SLOT * 0.5 * Math.pow(1 - p, 2.2);
  }

  /** ball drops from the rim into the pocket band with bouncing arcs */
  private ballRadiusAt(t: number): number {
    const dropStart = 0.62;
    const dropEnd = 0.82;
    if (t < dropStart) return 0.925;
    if (t < dropEnd) {
      const u = (t - dropStart) / (dropEnd - dropStart);
      // accelerating fall, then caught by the pocket band
      return 0.925 + (this.REST_RADIUS - 0.925) * (u * u * (3 - 2 * u));
    }
    const p = (t - dropEnd) / (1 - dropEnd);
    // three shrinking radial hops as it settles
    return this.REST_RADIUS + Math.abs(Math.sin(p * Math.PI * 3)) * 0.045 * Math.pow(1 - p, 1.6);
  }

  private draw(now: number): void {
    const ctx = this.ctx;
    const size = parseFloat(this.canvas.style.width);
    const c = size / 2;
    const R = c - 6;

    ctx.clearRect(0, 0, size, size);

    // ambient halo
    const halo = ctx.createRadialGradient(c, c, R * 0.6, c, c, R * 1.05);
    halo.addColorStop(0, "rgba(201,162,39,0.0)");
    halo.addColorStop(0.8, "rgba(201,162,39,0.10)");
    halo.addColorStop(1, "rgba(201,162,39,0)");
    ctx.fillStyle = halo;
    ctx.beginPath();
    ctx.arc(c, c, R * 1.03, 0, TAU);
    ctx.fill();

    // drop shadow
    ctx.save();
    ctx.beginPath();
    ctx.ellipse(c, c + R * 0.06, R * 0.99, R * 0.96, 0, 0, TAU);
    ctx.fillStyle = "rgba(0,0,0,0.55)";
    ctx.filter = "blur(14px)";
    ctx.fill();
    ctx.restore();

    // ---- outer rim: layered wood + gold inlay ----
    this.ring(c, R, R * 0.985, { inner: "#4a2f1b", outer: "#24160c" }, 0.0, TAU);
    this.ring(c, R * 0.972, R * 0.94, { inner: "#7a4f2d", outer: "#50331c" }, 0.0, TAU);

    // polished gold inlay with a slow rotating sheen
    const sheenAngle = now * 0.35;
    const goldGrad = ctx.createConicGradient ? ctx.createConicGradient(sheenAngle, c, c) : null;
    ctx.beginPath();
    ctx.arc(c, c, R * 0.935, 0, TAU);
    ctx.arc(c, c, R * 0.905, 0, TAU, true);
    if (goldGrad) {
      goldGrad.addColorStop(0, "#8b6a1f");
      goldGrad.addColorStop(0.12, "#e8cf86");
      goldGrad.addColorStop(0.25, "#a9832b");
      goldGrad.addColorStop(0.5, "#f2e3ac");
      goldGrad.addColorStop(0.62, "#9a7724");
      goldGrad.addColorStop(0.82, "#e5c979");
      goldGrad.addColorStop(1, "#8b6a1f");
      ctx.fillStyle = goldGrad;
    } else {
      ctx.fillStyle = "#c9a227";
    }
    ctx.fill("evenodd");

    // inner rim shadow
    this.ring(c, R * 0.905, R * 0.885, { inner: "rgba(0,0,0,0.65)", outer: "rgba(0,0,0,0.25)" }, 0, TAU);

    // ---- pocket ring ----
    const pocketOuter = R * 0.875;
    const pocketInner = R * 0.46;
    for (let i = 0; i < 38; i++) {
      const n = ORDER[i];
      const a0 = this.wheelAngle + i * SLOT - SLOT / 2 - Math.PI / 2;
      const a1 = a0 + SLOT;
      const col = pocketColor(n);

      // wedge with radial well gradient
      const g = ctx.createRadialGradient(c, c, pocketInner, c, c, pocketOuter);
      g.addColorStop(0, col.top);
      g.addColorStop(1, col.bottom);
      ctx.beginPath();
      ctx.arc(c, c, pocketOuter, a0, a1);
      ctx.arc(c, c, pocketInner, a1, a0, true);
      ctx.closePath();
      ctx.fillStyle = g;
      ctx.fill();

      // inner edge shading (depth)
      ctx.beginPath();
      ctx.arc(c, c, pocketOuter, a0, a1);
      ctx.arc(c, c, pocketOuter - R * 0.018, a1, a0, true);
      ctx.closePath();
      ctx.fillStyle = "rgba(0,0,0,0.28)";
      ctx.fill();

      // gold separators
      ctx.beginPath();
      ctx.moveTo(c + Math.cos(a0) * pocketInner, c + Math.sin(a0) * pocketInner);
      ctx.lineTo(c + Math.cos(a0) * pocketOuter, c + Math.sin(a0) * pocketOuter);
      ctx.strokeStyle = "rgba(232,207,134,0.55)";
      ctx.lineWidth = 1.2;
      ctx.stroke();

      // number plate
      const am = a0 + SLOT / 2;
      const tr = pocketOuter - R * 0.052;
      ctx.save();
      ctx.translate(c + Math.cos(am) * tr, c + Math.sin(am) * tr);
      ctx.rotate(am + Math.PI / 2);
      ctx.fillStyle = "rgba(0,0,0,0.45)";
      ctx.font = `800 ${Math.max(10, R * 0.052)}px "Segoe UI", system-ui, sans-serif`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(labelOf(n), 0.6, 0.8);
      ctx.fillStyle = "#f6efdd";
      ctx.fillText(labelOf(n), 0, 0);
      ctx.restore();
    }

    // ---- inner cone / hub ----
    const cone = ctx.createRadialGradient(c - R * 0.18, c - R * 0.22, R * 0.02, c, c, pocketInner);
    cone.addColorStop(0, "#2f333c");
    cone.addColorStop(0.55, "#171a20");
    cone.addColorStop(1, "#0a0c10");
    ctx.beginPath();
    ctx.arc(c, c, pocketInner, 0, TAU);
    ctx.fillStyle = cone;
    ctx.fill();

    // spokes
    for (let i = 0; i < 8; i++) {
      const a = this.wheelAngle + (i * TAU) / 8;
      const grad = ctx.createLinearGradient(
        c + Math.cos(a) * R * 0.12, c + Math.sin(a) * R * 0.12,
        c + Math.cos(a) * R * 0.44, c + Math.sin(a) * R * 0.44,
      );
      grad.addColorStop(0, "rgba(232,207,134,0.55)");
      grad.addColorStop(1, "rgba(232,207,134,0.08)");
      ctx.beginPath();
      ctx.moveTo(c + Math.cos(a) * R * 0.075, c + Math.sin(a) * R * 0.075);
      ctx.lineTo(c + Math.cos(a) * R * 0.44, c + Math.sin(a) * R * 0.44);
      ctx.strokeStyle = grad;
      ctx.lineWidth = Math.max(2, R * 0.008);
      ctx.stroke();
    }

    // hub rings + monogram
    this.ring(c, R * 0.17, R * 0.145, { inner: "#f0d98a", outer: "#a9832b" }, 0, TAU);
    this.ring(c, R * 0.135, R * 0.11, { inner: "#3b2f12", outer: "#17130a" }, 0, TAU);
    ctx.fillStyle = "#e8cf86";
    ctx.font = `700 ${Math.max(9, R * 0.04)}px "Segoe UI", system-ui, sans-serif`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText("PR", c, c + 1);

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
    const br = Math.max(4.5, R * 0.027);

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
    const ballGrad = ctx.createRadialGradient(bx - br * 0.35, by - br * 0.4, br * 0.15, bx, by, br);
    ballGrad.addColorStop(0, "#ffffff");
    ballGrad.addColorStop(0.55, "#f2f2f2");
    ballGrad.addColorStop(1, "#b9bdc4");
    ctx.beginPath();
    ctx.arc(bx, by, br, 0, TAU);
    ctx.fillStyle = ballGrad;
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,0.35)";
    ctx.lineWidth = 1;
    ctx.stroke();

    // specular
    ctx.beginPath();
    ctx.arc(bx - br * 0.33, by - br * 0.38, br * 0.22, 0, TAU);
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

  private ring(c: number, rOuter: number, rInner: number, colors: { inner: string; outer: string }, a0: number, a1: number): void {
    const ctx = this.ctx;
    const g = ctx.createRadialGradient(c, c, rInner, c, c, rOuter);
    g.addColorStop(0, colors.inner);
    g.addColorStop(1, colors.outer);
    ctx.beginPath();
    ctx.arc(c, c, rOuter, a0, a1);
    ctx.arc(c, c, rInner, a1, a0, true);
    ctx.closePath();
    ctx.fillStyle = g;
    ctx.fill();
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
