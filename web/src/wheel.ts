// Procedural European roulette wheel with a physically-flavoured spin.
// No image assets: everything is drawn with canvas 2D. We own this art.

const ORDER = [
  0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10,
  5, 24, 16, 33, 1, 20, 14, 31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26,
];

const RED = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);

function colorOf(n: number): string {
  if (n === 0) return "#0f9d58";
  return RED.has(n) ? "#c0392b" : "#1b1b1f";
}

const TAU = Math.PI * 2;
const POCKET = TAU / 37;

interface SpinState {
  startTime: number;
  duration: number;         // ms
  wheelFrom: number;
  wheelTo: number;          // radians (mod TAU irrelevant; absolute)
  ballFrom: number;
  ballTo: number;           // final ball angle = wheelTo + pocket angle
  result: number;
  bouncing: boolean;
}

export class Wheel {
  private canvas: HTMLCanvasElement;
  private ctx: CanvasRenderingContext2D;
  private raf = 0;
  private wheelAngle = 0;         // absolute rotation of the wheel
  private idleSpeed = (TAU / 60) * 0.25; // rad/s idle drift
  private spin: SpinState | null = null;
  private lastFrame = 0;
  private onSettle?: (result: number) => void;

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

  dispose() {
    cancelAnimationFrame(this.raf);
  }

  private resize() {
    const parent = this.canvas.parentElement!;
    const size = Math.max(260, Math.min(parent.clientWidth, 560));
    const dpr = window.devicePixelRatio || 1;
    this.canvas.style.width = `${size}px`;
    this.canvas.style.height = `${size}px`;
    this.canvas.width = size * dpr;
    this.canvas.height = size * dpr;
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  /** Kick off a spin animation that settles on `result`. */
  spinTo(result: number, durationMs = 6500): void {
    if (this.spin) return; // already spinning
    const pocketIndex = ORDER.indexOf(result);
    const pocketAngle = pocketIndex * POCKET; // angle of the winning pocket in wheel-local space

    const wheelFrom = this.wheelAngle;
    const wheelTurns = 3 + Math.random() * 1.5;
    const wheelTo = wheelFrom + wheelTurns * TAU;

    const ballFrom = this.wheelAngle + Math.PI; // start opposite-ish
    const ballTurns = 6 + Math.random() * 2;
    // final ball ABSOLUTE angle such that (ball - wheelTo) ≡ pocketAngle (mod TAU)
    const rawBallTo = wheelTo + pocketAngle;
    const ballTo = rawBallTo + ballTurns * TAU;

    this.spin = {
      startTime: performance.now(),
      duration: durationMs,
      wheelFrom,
      wheelTo,
      ballFrom,
      ballTo,
      result,
      bouncing: false,
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
      // settle exactly: wheel at wheelTo, ball in the winning pocket
      this.wheelAngle = s.wheelTo;
      this.ballAngle = s.ballFrom + (s.ballTo - s.ballFrom);
      this.lastResult = s.result;
      this.spin = null;
      this.onSettle?.(s.result);
      return;
    }
    // wheel: strong ease-out
    this.wheelAngle = s.wheelFrom + (s.wheelTo - s.wheelFrom) * this.easeOutCubic(Math.min(t, 1));

    // ball: travels further and decelerates slightly later, with a small
    // damped bounce in the final 15% so it feels like it drops into a pocket
    let b: number;
    if (t < 0.85) {
      const bt = t / 0.85;
      b = s.ballFrom + (s.ballTo - s.ballFrom) * (1 - Math.pow(1 - bt, 2.2));
    } else {
      const base = s.ballFrom + (s.ballTo - s.ballFrom);
      const phase = (t - 0.85) / 0.15;
      const wobble = Math.sin(phase * Math.PI * 3) * POCKET * 0.9 * (1 - phase);
      b = base + wobble;
    }
    this.ballAngle = b;
  }

  private ballAngle = 0;

  private draw(): void {
    const ctx = this.ctx;
    const size = parseFloat(this.canvas.style.width);
    const c = size / 2;
    const R = c - 4;

    ctx.clearRect(0, 0, size, size);

    // outer wooden rim
    const rim = ctx.createRadialGradient(c, c, R * 0.82, c, c, R);
    rim.addColorStop(0, "#5a3a22");
    rim.addColorStop(0.75, "#7a4f2d");
    rim.addColorStop(1, "#3c2517");
    ctx.beginPath();
    ctx.arc(c, c, R, 0, TAU);
    ctx.fillStyle = rim;
    ctx.fill();

    // pocket ring
    const pocketR = R * 0.78;
    for (let i = 0; i < 37; i++) {
      const n = ORDER[i];
      const a0 = this.wheelAngle + i * POCKET - POCKET / 2 - Math.PI / 2;
      ctx.beginPath();
      ctx.moveTo(c + Math.cos(a0) * R * 0.55, c + Math.sin(a0) * R * 0.55);
      ctx.arc(c, c, pocketR, a0, a0 + POCKET);
      ctx.closePath();
      ctx.fillStyle = colorOf(n);
      ctx.fill();
      ctx.strokeStyle = "rgba(218,179,113,0.55)";
      ctx.lineWidth = 1;
      ctx.stroke();

      // number label
      const am = a0 + POCKET / 2;
      const tx = c + Math.cos(am) * (pocketR - R * 0.06);
      const ty = c + Math.sin(am) * (pocketR - R * 0.06);
      ctx.save();
      ctx.translate(tx, ty);
      ctx.rotate(am + Math.PI / 2);
      ctx.fillStyle = "#f5f0e6";
      ctx.font = `600 ${Math.max(9, R * 0.052)}px system-ui, sans-serif`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(String(n), 0, 0);
      ctx.restore();

      // separators
      ctx.beginPath();
      ctx.moveTo(c + Math.cos(a0) * R * 0.55, c + Math.sin(a0) * R * 0.55);
      ctx.lineTo(c + Math.cos(a0) * pocketR, c + Math.sin(a0) * pocketR);
      ctx.strokeStyle = "rgba(218,179,113,0.5)";
      ctx.stroke();
    }

    // inner cone
    const cone = ctx.createRadialGradient(c, c, 0, c, c, R * 0.55);
    cone.addColorStop(0, "#2a2a30");
    cone.addColorStop(1, "#101014");
    ctx.beginPath();
    ctx.arc(c, c, R * 0.55, 0, TAU);
    ctx.fillStyle = cone;
    ctx.fill();

    // decorative spokes
    for (let i = 0; i < 8; i++) {
      const a = this.wheelAngle + (i * TAU) / 8;
      ctx.beginPath();
      ctx.moveTo(c + Math.cos(a) * R * 0.08, c + Math.sin(a) * R * 0.08);
      ctx.lineTo(c + Math.cos(a) * R * 0.5, c + Math.sin(a) * R * 0.5);
      ctx.strokeStyle = "rgba(218,179,113,0.35)";
      ctx.lineWidth = 2;
      ctx.stroke();
    }

    // hub
    ctx.beginPath();
    ctx.arc(c, c, R * 0.1, 0, TAU);
    ctx.fillStyle = "#c9a227";
    ctx.fill();

    // ball
    let ballR = R * 0.62;
    let ballA = this.ballAngle;
    if (!this.spin) {
      // resting ball sits in the last pocket (or opposite if none yet)
      ballA = this.wheelAngle + (this.lastResult >= 0 ? ORDER.indexOf(this.lastResult) * POCKET : Math.PI);
    } else {
      // ball starts high on the rim and spirals down as it slows
      const s = this.spin;
      const t = Math.min((performance.now() - s.startTime) / s.duration, 1);
      ballR = R * (0.9 - 0.28 * this.easeOutCubic(t));
    }
    if (this.lastResult >= 0) {
      const bx = c + Math.cos(ballA) * ballR;
      const by = c + Math.sin(ballA) * ballR;
      ctx.beginPath();
      ctx.arc(bx, by, Math.max(4, R * 0.028), 0, TAU);
      ctx.fillStyle = "#f8f8f8";
      ctx.shadowColor = "rgba(0,0,0,0.6)";
      ctx.shadowBlur = 6;
      ctx.fill();
      ctx.shadowBlur = 0;
    }

    // result badge
    if (this.lastResult >= 0 && !this.spin) {
      ctx.beginPath();
      ctx.arc(c, c, R * 0.19, 0, TAU);
      ctx.fillStyle = "rgba(0,0,0,0.55)";
      ctx.fill();
      ctx.fillStyle = colorOf(this.lastResult);
      ctx.font = `700 ${R * 0.16}px system-ui, sans-serif`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.fillText(String(this.lastResult), c, c + 1);
    }
  }

  private lastResult = -1;

  /** Public getter used by UI to know if a spin animation is running. */
  get isSpinning(): boolean {
    return this.spin !== null;
  }
}
