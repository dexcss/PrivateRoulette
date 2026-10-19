// Tiny synthesized sound effects — no audio assets, pure WebAudio.

export class Sfx {
  private ctx: AudioContext | null = null;
  muted = localStorage.getItem("pr_muted") === "1";

  private ensure(): AudioContext | null {
    try {
      if (!this.ctx) this.ctx = new AudioContext();
      if (this.ctx.state === "suspended") void this.ctx.resume();
      return this.ctx;
    } catch {
      return null;
    }
  }

  setMuted(m: boolean) {
    this.muted = m;
    localStorage.setItem("pr_muted", m ? "1" : "0");
  }

  private blip(freq: number, dur: number, type: OscillatorType, gain = 0.06, delay = 0) {
    if (this.muted) return;
    const ctx = this.ensure();
    if (!ctx) return;
    const t0 = ctx.currentTime + delay;
    const osc = ctx.createOscillator();
    const g = ctx.createGain();
    osc.type = type;
    osc.frequency.setValueAtTime(freq, t0);
    g.gain.setValueAtTime(gain, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    osc.connect(g).connect(ctx.destination);
    osc.start(t0);
    osc.stop(t0 + dur + 0.02);
  }

  chip() {
    this.blip(520, 0.04, "square", 0.04);
  }

  tick() {
    this.blip(1400, 0.025, "square", 0.025);
  }

  ready() {
    this.blip(660, 0.06, "sine", 0.06);
    this.blip(880, 0.08, "sine", 0.06, 0.07);
  }

  win() {
    this.blip(523, 0.12, "triangle", 0.08);
    this.blip(659, 0.12, "triangle", 0.08, 0.11);
    this.blip(784, 0.2, "triangle", 0.08, 0.22);
  }

  lose() {
    this.blip(220, 0.16, "sine", 0.05);
    this.blip(165, 0.22, "sine", 0.05, 0.12);
  }

  error() {
    this.blip(180, 0.1, "square", 0.05);
  }
}
