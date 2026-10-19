// Shared state types + WebSocket envelope protocol.

export type Role = "dealer" | "player" | "spectator";

export interface PublicPlayer {
  id: string;
  name: string;
  display_name: string;
  color: string;
  chip_style: string;
  bets: Record<string, number>;
  ready: boolean;
  away: boolean;
  is_vip: boolean;
  is_freeplay: boolean;
  last_round_net: number;
  balance: number;
}

export interface GameStateMsg {
  spinning: boolean;
  table_locked: boolean;
  awaiting_dice?: boolean;
  spin_mode: "control" | "random" | "manual";
  control_number: string;
  creator_name: string;
  creator_homeworld: string;
  game_status: string;
  max_bet_inner: number;
  max_bet_outer: number;
  max_bet_inner_vip: number;
  max_bet_outer_vip: number;
  chip_denominations: number[];
  players: PublicPlayer[];
}

export interface PublicBetsMsg {
  players: PublicPlayer[];
}

export interface SpinMsg {
  result: number;
  color: string;
  proof: {
    dice_roll: number | null;
    control_number: string;
    hash: string;
    seed: number;
    result: number;
  };
  control_number: string;
}

export interface RoundResultMsg {
  result: number;
  color: string;
  dice_roll: number | null;
  balances: Record<string, number>;
}

export interface HistoryEntry {
  ts: string;
  result: number;
  color: string;
}

export interface ChatMsg {
  id: string;
  from_id: string;
  from_name: string;
  text: string;
  ts: string;
  role: "dealer" | "player" | "system";
  color: string;
}

export interface MeState {
  playerId: string;
  displayName: string;
}

export const RED_NUMS = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);

export const DOUBLE_ZERO = 37;

export function colorOf(n: number): "red" | "black" | "green" {
  if (n === 0 || n === DOUBLE_ZERO) return "green";
  return RED_NUMS.has(n) ? "red" : "black";
}

export function displayNumber(n: number): string {
  return n === DOUBLE_ZERO ? "00" : String(n);
}

export const PAYOUTS: Record<string, number> = {
  "n-": 36, split: 18, street: 12, corner: 9, sixline: 6,
  firstfour: 9, dozen: 3, col: 3, red: 2, black: 2, odd: 2, even: 2, low: 2, high: 2,
};

// ---------- connection ----------

export type Handler = (d: any) => void;

export class Api {
  ws: WebSocket | null = null;
  handlers = new Map<string, Set<Handler>>();
  openHandlers = new Set<() => void>();
  closeHandlers = new Set<() => void>();
  private url: string;
  private closedByUs = false;
  private retries = 0;

  constructor(url: string) {
    this.url = url;
  }

  setUrl(url: string) {
    this.url = url;
  }

  connect(): void {
    this.closedByUs = false;
    this.ws = new WebSocket(this.url);
    this.ws.onopen = () => {
      this.retries = 0;
      this.openHandlers.forEach((h) => h());
    };
    this.ws.onclose = () => {
      this.closeHandlers.forEach((h) => h());
      if (!this.closedByUs) {
        this.retries++;
        const delay = Math.min(1000 * this.retries, 10000);
        setTimeout(() => this.connect(), delay);
      }
    };
    this.ws.onmessage = (ev) => {
      try {
        const env = JSON.parse(ev.data as string);
        const set = this.handlers.get(env.t);
        if (set) set.forEach((h) => h(env.d));
        const anySet = this.handlers.get("*");
        if (anySet) anySet.forEach((h) => h(env));
      } catch {
        /* ignore malformed */
      }
    };
  }

  disconnect() {
    this.closedByUs = true;
    this.ws?.close();
    this.ws = null;
  }

  on(t: string, h: Handler): void {
    if (!this.handlers.has(t)) this.handlers.set(t, new Set());
    this.handlers.get(t)!.add(h);
  }

  onOpen(h: () => void): void {
    this.openHandlers.add(h);
  }

  onClose(h: () => void): void {
    this.closeHandlers.add(h);
  }

  send(t: string, d: unknown = {}): void {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify({ t, d }));
    }
  }
}
