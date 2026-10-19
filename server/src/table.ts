// Private Roulette — TableRoom Durable Object.
// One instance per game. Owns authoritative state: players, balances, bets,
// rounds, ledger, chat. Speaks a simple JSON envelope protocol over WebSockets:
//   { "t": "<event name>", "d": { ...payload } }
// Both directions use the same envelope.

import { DurableObject } from "cloudflare:workers";
import {
  colorOf, computeResult, cryptoInt, cryptoRandomHex,
  isValidBetTarget, isInsideBet, payoutMultiplierForTarget, playerColor,
} from "./fairness";

export interface Env {
  DEALER_TOKEN: string;
  TABLE: DurableObjectNamespace;
}

// ---------- types ----------

interface Player {
  id: string;
  name: string;            // "Char@World" as entered by the dealer
  display_name: string;
  balance: number;
  ready: boolean;
  away: boolean;
  bets: Record<string, number>;
  color: string;
  last_round_net: number;
  is_vip: boolean;
  is_freeplay: boolean;
  player_key: string;
  joined_at: string;
  left_at: string | null;
  cash_out_amount: number | null;
  buy_in_total: number;
}

interface ChatMsg {
  id: string;
  from_id: string;
  from_name: string;
  text: string;
  ts: string;
  role: "dealer" | "player" | "system";
  color: string;
}

interface RoundBetRecord {
  round_number: number;
  player_id: string;
  player_name: string;
  bet_type: string;
  bet_amount: number;
  payout_amount: number;
  placed_at: string;
}

interface RoundRecord {
  round_number: number;
  winning_number: number;
  winning_color: string;
  dice_roll: number | null;
  total_bets: number;
  total_payouts: number;
  spun_at: string;
  proof: unknown;
}

interface AdjustmentRecord {
  player_id: string;
  player_name: string;
  adjustment_amount: number;
  reason: string;
  adjusted_by: string;
  adjusted_at: string;
}

interface GameState {
  game_id: string;
  dealer_name: string;
  dealer_world: string;
  spin_mode: "control" | "random" | "manual";
  control_number: string;
  max_bet_inner: number;
  max_bet_outer: number;
  max_bet_inner_vip: number;
  max_bet_outer_vip: number;
  chip_denominations: number[];
  game_status: "active" | "finished" | "payouts_complete" | "deleted";
  table_locked: boolean;
  spinning: boolean;
  awaiting_dice: boolean;
  round_number: number;
  created_at: string;
  players: Player[];
  chat: ChatMsg[];
  history: { ts: string; result: number; color: string }[];
  rounds: RoundRecord[];
  all_bets: RoundBetRecord[];
  adjustments: AdjustmentRecord[];
  total_tips: number;
}

interface Conn {
  ws: WebSocket;
  role: "dealer" | "player" | "spectator";
  playerId: string | null;
  quit: boolean;
  lastChatTs: number;
}

const MAX_CHAT = 60;
const BET_TARGET_MAX_LEN = 40;

function nowIso(): string {
  return new Date().toISOString();
}

function publicPlayer(p: Player) {
  return {
    id: p.id,
    name: p.name,
    display_name: p.display_name,
    color: p.color,
    chip_style: "classic",
    bets: p.bets,
    ready: p.ready,
    away: p.away,
    is_vip: p.is_vip,
    is_freeplay: p.is_freeplay,
    last_round_net: p.last_round_net,
    balance: p.balance,
  };
}

export class TableRoom extends DurableObject<Env> {
  private state: GameState | null = null;
  private conns = new Map<WebSocket, Conn>();

  // ---------- storage ----------

  private async load(gameId: string): Promise<GameState | null> {
    if (this.state && this.state.game_id === gameId) return this.state;
    const stored = await this.ctx.storage.get<GameState>(`game:${gameId}`);
    this.state = stored ?? null;
    return this.state;
  }

  private async save(): Promise<void> {
    if (!this.state) return;
    await this.ctx.storage.put(`game:${this.state.game_id}`, this.state);
  }

  private async listGameSummaries(): Promise<unknown[]> {
    const out: unknown[] = [];
    const entries = await this.ctx.storage.list<GameState>({ prefix: "game:" });
    for (const [, g] of entries) {
      if (g.game_status === "deleted") continue;
      out.push({
        id: g.game_id,
        theme: null,
        theme_display_name: null,
        players: g.players.filter((p) => !p.left_at).length,
        status: g.game_status,
        spinning: g.spinning,
        created_ts: g.created_at,
        creator_name: g.dealer_name,
        creator_homeworld: g.dealer_world,
        spin_mode: g.spin_mode,
        control_number: g.control_number,
      });
    }
    return out.sort((a, b) => String((b as any).created_ts).localeCompare(String((a as any).created_ts)));
  }

  // ---------- websocket plumbing ----------

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/ws") {
      const pair = new WebSocketPair();
      this.ctx.acceptWebSocket(pair[1]);
      this.conns.set(pair[1], { ws: pair[1], role: "spectator", playerId: null, quit: false, lastChatTs: 0 });
      return new Response(null, { status: 101, webSocket: pair[0] });
    }
    return new Response("not found", { status: 404 });
  }

  private send(ws: WebSocket, t: string, d: unknown): void {
    try {
      ws.send(JSON.stringify({ t, d }));
    } catch {
      // socket may be closing
    }
  }

  private broadcast(t: string, d: unknown, includeDealer = true): void {
    for (const [ws, conn] of this.conns) {
      if (conn.quit) continue;
      if (!includeDealer && conn.role === "dealer") continue;
      this.send(ws, t, d);
    }
  }

  private broadcastDealerOnly(t: string, d: unknown): void {
    for (const [ws, conn] of this.conns) {
      if (conn.role === "dealer" && !conn.quit) this.send(ws, t, d);
    }
  }

  private err(ws: WebSocket, message: string): void {
    this.send(ws, "dealer_error", { message });
  }

  private systemChat(text: string): void {
    if (!this.state) return;
    const msg: ChatMsg = {
      id: cryptoRandomHex(5),
      from_id: "system",
      from_name: "System",
      text,
      ts: nowIso(),
      role: "system",
      color: "#ffcc00",
    };
    this.state.chat.push(msg);
    if (this.state.chat.length > MAX_CHAT) this.state.chat.splice(0, this.state.chat.length - MAX_CHAT);
    this.broadcast("chat_message", msg);
  }

  private dealerChat(text: string): void {
    if (!this.state) return;
    const msg: ChatMsg = {
      id: cryptoRandomHex(5),
      from_id: "dealer",
      from_name: this.state.dealer_name,
      text,
      ts: nowIso(),
      role: "dealer",
      color: "#ffcc00",
    };
    this.state.chat.push(msg);
    if (this.state.chat.length > MAX_CHAT) this.state.chat.splice(0, this.state.chat.length - MAX_CHAT);
    this.broadcast("chat_message", msg);
  }

  private snapshotFor(conn: Conn): void {
    const g = this.state;
    if (!g) return;

    if (conn.role === "dealer") {
      this.send(conn.ws, "game_state", {
        spinning: g.spinning,
        table_locked: g.table_locked,
        theme: null,
        theme_display_name: null,
        creator_name: g.dealer_name,
        creator_homeworld: g.dealer_world,
        spin_mode: g.spin_mode,
        control_number: g.control_number,
        max_bet_inner: g.max_bet_inner,
        max_bet_outer: g.max_bet_outer,
        max_bet_inner_vip: g.max_bet_inner_vip,
        max_bet_outer_vip: g.max_bet_outer_vip,
        chip_denominations: g.chip_denominations,
        game_status: g.game_status,
        chat_rate_limit: { max_messages: 1, per_seconds: 2 },
        players: g.players,
        awaiting_dice: g.awaiting_dice,
      });
      this.send(conn.ws, "public_bets", { players: g.players.map(publicPlayer) });
    } else {
      // players/spectators never see other players' keys
      this.send(conn.ws, "game_state", {
        spinning: g.spinning,
        table_locked: g.table_locked,
        theme: null,
        theme_display_name: null,
        creator_name: g.dealer_name,
        creator_homeworld: g.dealer_world,
        spin_mode: g.spin_mode,
        control_number: g.control_number,
        max_bet_inner: g.max_bet_inner,
        max_bet_outer: g.max_bet_outer,
        max_bet_inner_vip: g.max_bet_inner_vip,
        max_bet_outer_vip: g.max_bet_outer_vip,
        chip_denominations: g.chip_denominations,
        game_status: g.game_status,
        chat_rate_limit: { max_messages: 1, per_seconds: 2 },
        players: g.players.map(publicPlayer),
      });
      this.send(conn.ws, "public_bets", { players: g.players.map(publicPlayer) });
    }
    this.send(conn.ws, "table_history", { history: g.history.slice(-25) });
    this.send(conn.ws, "chat_history", { messages: g.chat });
  }

  // ---------- message handling ----------

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    const conn = this.conns.get(ws);
    if (!conn || conn.quit) return;

    let env: { t?: string; d?: any };
    try {
      env = JSON.parse(typeof raw === "string" ? raw : new TextDecoder().decode(raw));
    } catch {
      return;
    }
    const t = env.t ?? "";
    const d = env.d ?? {};

    // ---- hello / join ----
    if (t === "hello") {
      await this.handleHello(conn, ws, d);
      return;
    }

    if (!this.state) {
      this.err(ws, "no game");
      return;
    }
    const g = this.state;

    try {
      switch (t) {
        // ================= dealer =================
        case "ping": this.send(ws, "pong", {}); break;
        case "list_games": this.send(ws, "games_list", { games: await this.listGameSummaries() }); break;
        case "create_game": await this.createGame(conn, ws, d); break;
        case "join_game": await this.joinGame(conn, ws, d); break;
        case "add_player": await this.addPlayer(ws, d); break;
        case "adjust_balance": await this.adjustBalance(ws, d); break;
        case "record_payout": await this.recordPayout(ws, d); break;
        case "request_spin": await this.requestSpin(ws); break;
        case "spin": await this.spin(ws, d); break;
        case "clear_bets": await this.clearBets(ws); break;
        case "rebet": await this.rebet(ws); break;
        case "send_chat":
          if (typeof d.text === "string" && d.text.trim().length > 0) this.dealerChat(d.text.trim().slice(0, 300));
          break;
        case "get_ledger": this.send(ws, "ledger", this.ledgerView()); break;
        case "end_game": await this.endGame(ws); break;
        case "delete_game": await this.deleteGame(ws); break;

        // ================= player =================
        case "place_bet": await this.placeBet(conn, ws, d); break;
        case "remove_bet": await this.removeBet(conn, ws, d); break;
        case "clear_my_bets": await this.clearMyBets(conn, ws); break;
        case "rebet_mine": await this.rebetMine(conn, ws); break;
        case "set_ready": await this.setReady(conn, ws, d); break;
        case "set_away": await this.setAway(conn, ws, d); break;
        case "cashout": await this.cashout(conn, ws); break;
        case "send_player_chat": await this.playerChat(conn, ws, d); break;

        default:
          this.err(ws, `unknown event ${t}`);
      }
    } catch (e: any) {
      this.err(ws, e?.message ?? "internal error");
    }
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    this.conns.delete(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    this.conns.delete(ws);
  }

  private async handleHello(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    const token = typeof d.token === "string" ? d.token : "";
    const expected = (this.env as any).DEALER_TOKEN as string | undefined;

    if (d.role === "dealer") {
      if (!expected || token !== expected) {
        this.err(ws, "invalid dealer token");
        ws.close(1008, "unauthorized");
        conn.quit = true;
        return;
      }
      conn.role = "dealer";
      this.send(ws, "hello_ok", { role: "dealer" });
      this.send(ws, "games_list", { games: await this.listGameSummaries() });
      return;
    }

    const gameId = String(d.game_id ?? "");
    const g = await this.load(gameId);
    if (!g) {
      this.err(ws, "game not found");
      ws.close(1008, "no game");
      conn.quit = true;
      return;
    }

    if (d.role === "player") {
      const key = String(d.player_key ?? "");
      const player = g.players.find((p) => p.player_key === key && !p.left_at);
      if (!player) {
        this.err(ws, "invalid player key");
        ws.close(1008, "unauthorized");
        conn.quit = true;
        return;
      }
      conn.role = "player";
      conn.playerId = player.id;
      this.send(ws, "hello_ok", { role: "player", player_id: player.id, display_name: player.display_name, balance: player.balance });
      this.snapshotFor(conn);
      this.broadcast("player_joined", { player_id: player.id });
      return;
    }

    // spectator
    conn.role = "spectator";
    this.send(ws, "hello_ok", { role: "spectator" });
    this.snapshotFor(conn);
  }

  // ================= dealer operations =================

  private async createGame(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    if (conn.role !== "dealer") return this.err(ws, "dealer only");
    const gameId = cryptoRandomHex(4);
    const mode = ["control", "random", "manual"].includes(d.spin_mode) ? d.spin_mode : "control";
    const g: GameState = {
      game_id: gameId,
      dealer_name: String(d.dealer_name ?? "Dealer"),
      dealer_world: String(d.dealer_world ?? ""),
      spin_mode: mode,
      control_number: cryptoRandomHex(8),
      max_bet_inner: clampNum(d.max_bet_inner, 1, 100_000_000, 500),
      max_bet_outer: clampNum(d.max_bet_outer, 1, 100_000_000, 250),
      max_bet_inner_vip: clampNum(d.max_bet_inner_vip, 1, 100_000_000, 1000),
      max_bet_outer_vip: clampNum(d.max_bet_outer_vip, 1, 100_000_000, 500),
      chip_denominations: Array.isArray(d.chip_denominations) && d.chip_denominations.length > 0
        ? d.chip_denominations.map((x: any) => clampNum(x, 1, 10_000_000, 25))
        : [25, 100, 500, 1000],
      game_status: "active",
      table_locked: false,
      spinning: false,
      awaiting_dice: false,
      round_number: 0,
      created_at: nowIso(),
      players: [],
      chat: [],
      history: [],
      rounds: [],
      all_bets: [],
      adjustments: [],
      total_tips: 0,
    };
    this.state = g;
    await this.save();
    this.send(ws, "game_created", {
      game_id: g.game_id,
      spin_mode: g.spin_mode,
      control_number: g.control_number,
      max_bet_inner: g.max_bet_inner,
      max_bet_outer: g.max_bet_outer,
      max_bet_inner_vip: g.max_bet_inner_vip,
      max_bet_outer_vip: g.max_bet_outer_vip,
    });
    this.send(ws, "games_list", { games: await this.listGameSummaries() });
    this.snapshotFor(conn);
    this.systemChat(`Game ${g.game_id} created by ${g.dealer_name}. Welcome!`);
  }

  private async joinGame(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    if (conn.role !== "dealer") return this.err(ws, "dealer only");
    const g = await this.load(String(d.game_id ?? ""));
    if (!g) return this.err(ws, "game not found");
    this.state = g;
    this.send(ws, "game_joined", { game_id: g.game_id });
    this.snapshotFor(conn);
  }

  private async addPlayer(ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    const name = String(d.name ?? "").trim();
    if (name.length === 0) return this.err(ws, "player name required");
    const displayName = String(d.display_name ?? name.split("@")[0]).trim();
    const bankroll = clampNum(d.bankroll, 0, 100_000_000_000, 0);

    const player: Player = {
      id: cryptoRandomHex(4),
      name,
      display_name: displayName,
      balance: bankroll,
      ready: false,
      away: false,
      bets: {},
      color: "#000000",
      last_round_net: 0,
      is_vip: !!d.is_vip,
      is_freeplay: !!d.is_freeplay,
      player_key: cryptoRandomHex(22),
      joined_at: nowIso(),
      left_at: null,
      cash_out_amount: null,
      buy_in_total: bankroll,
    };
    player.color = playerColor(player.id);
    g.players.push(player);
    await this.save();

    this.send(ws, "player_created", {
      id: player.id,
      name: player.name,
      display_name: player.display_name,
      balance: player.balance,
      game_id: g.game_id,
      color: player.color,
      player_key: player.player_key,
    });
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.broadcast("game_state", dealerStateView(g));
    this.systemChat(`${displayName} joined the table.`);
  }

  private async adjustBalance(ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    const p = g.players.find((x) => x.id === d.player_id && !x.left_at);
    if (!p) return this.err(ws, "player not found");
    const delta = Math.trunc(Number(d.delta ?? 0));
    if (!Number.isFinite(delta) || delta === 0) return this.err(ws, "invalid delta");
    if (delta < 0 && p.balance + delta < 0) return this.err(ws, "balance cannot go negative");

    p.balance += delta;
    if (delta > 0) p.buy_in_total += delta;
    const rec: AdjustmentRecord = {
      player_id: p.id,
      player_name: p.name,
      adjustment_amount: delta,
      reason: String(d.reason ?? "Dealer adjustment"),
      adjusted_by: g.dealer_name,
      adjusted_at: nowIso(),
    };
    g.adjustments.push(rec);
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.broadcast("game_state", dealerStateView(g));
    this.send(ws, "dealer_ok", { message: `${p.display_name} balance ${delta > 0 ? "+" : ""}${delta}` });
  }

  private async recordPayout(ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    const p = g.players.find((x) => x.id === d.player_id && !x.left_at);
    if (!p) return this.err(ws, "player not found");
    const amount = Math.trunc(Number(d.amount ?? 0));
    if (amount <= 0 || amount > p.balance) return this.err(ws, "invalid payout amount");
    p.balance -= amount;
    g.total_tips += amount;
    g.adjustments.push({
      player_id: p.id,
      player_name: p.name,
      adjustment_amount: -amount,
      reason: String(d.reason ?? "Tip / recorded payout"),
      adjusted_by: g.dealer_name,
      adjusted_at: nowIso(),
    });
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.broadcast("game_state", dealerStateView(g));
    this.send(ws, "dealer_ok", { message: `Recorded ${amount} gil for ${p.display_name}` });
  }

  private async requestSpin(ws: WebSocket): Promise<void> {
    const g = this.state!;
    if (g.spinning || g.awaiting_dice) return this.err(ws, "spin already in progress");
    if (g.game_status !== "active") return this.err(ws, "game not active");
    g.table_locked = true;
    g.awaiting_dice = g.spin_mode === "control";
    await this.save();
    this.broadcast("dealer_spin_ready", {
      game_id: g.game_id,
      message: "Table locked and ready for spin command",
      spin_mode: g.spin_mode,
      control_number: g.control_number,
    });
    this.broadcast("game_state", dealerStateView(g));
  }

  private async spin(ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    if (!g.table_locked) return this.err(ws, "table not locked — request spin first");
    if (g.spinning) return this.err(ws, "spin already in progress");

    const hasBets = g.players.some((p) => !p.left_at && Object.keys(p.bets).length > 0);
    let forcedNumber: number | null = null;
    let diceRoll: number | null = null;

    if (g.spin_mode === "manual") {
      forcedNumber = Math.trunc(Number(d.number ?? -1));
      if (!(forcedNumber >= 0 && forcedNumber <= 36)) return this.err(ws, "manual spin needs number 0-36");
    } else if (g.spin_mode === "control") {
      diceRoll = Math.trunc(Number(d.dice_roll ?? -1));
      if (!(diceRoll >= 0 && diceRoll <= 999)) return this.err(ws, "control spin needs dice_roll 0-999");
    } else {
      diceRoll = cryptoInt(0, 999); // random mode: server rolls, still fully auditable
    }

    g.spinning = true;
    g.awaiting_dice = false;
    await this.save();
    this.broadcast("game_state", dealerStateView(g));

    const { result, proof } = await computeResult(diceRoll, g.control_number, forcedNumber);
    const color = colorOf(result);

    // ---- settle bets ----
    let totalBets = 0;
    let totalPayouts = 0;
    const roundNo = ++g.round_number;
    const placedAt = nowIso();

    for (const p of g.players) {
      if (p.left_at) continue;
      let net = 0;
      for (const [target, amount] of Object.entries(p.bets)) {
        const mult = payoutMultiplierForTarget(target, result);
        const payout = amount * mult;
        net += payout - amount;
        if (mult > 0) {
          p.balance += payout;
          totalPayouts += payout;
        }
        totalBets += amount;
        g.all_bets.push({
          round_number: roundNo,
          player_id: p.id,
          player_name: p.name,
          bet_type: target,
          bet_amount: amount,
          payout_amount: payout,
          placed_at: placedAt,
        });
      }
      p.last_round_net = net;
      p.bets = {};
      p.ready = false;
    }

    const historyEntry = { ts: nowIso(), result, color };
    g.history.push(historyEntry);
    g.rounds.push({
      round_number: roundNo,
      winning_number: result,
      winning_color: color,
      dice_roll: diceRoll,
      total_bets: totalBets,
      total_payouts: totalPayouts,
      spun_at: historyEntry.ts,
      proof,
    });
    g.table_locked = false;
    g.spinning = false;
    await this.save();

    this.broadcast("spin", { result, color, proof, control_number: g.control_number });
    this.broadcast("round_result", {
      result,
      color,
      dice_roll: diceRoll,
      balances: Object.fromEntries(g.players.filter((p) => !p.left_at).map((p) => [p.id, p.balance])),
    });
    this.broadcast("table_history", { history: g.history.slice(-25) });
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.broadcast("game_state", dealerStateView(g));

    const winners = g.players.filter((p) => !p.left_at && p.last_round_net > 0);
    if (winners.length > 0) {
      this.systemChat(`Number ${result} (${color}) wins! ${winners.map((w) => `${w.display_name} +${w.last_round_net.toLocaleString()}`).join(", ")}`);
    } else if (hasBets) {
      this.systemChat(`Number ${result} (${color}) wins! House takes the round.`);
    } else {
      this.systemChat(`Number ${result} (${color}). No bets this round.`);
    }
  }

  private async clearBets(ws: WebSocket): Promise<void> {
    const g = this.state!;
    for (const p of g.players) {
      if (!p.left_at) p.bets = {};
    }
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.send(ws, "dealer_ok", { message: "All bets cleared" });
  }

  private async rebet(ws: WebSocket): Promise<void> {
    const g = this.state!;
    // rebuild last round's bets per player from the ledger
    const lastRound = g.round_number;
    const lastBets = g.all_bets.filter((b) => b.round_number === lastRound);
    for (const p of g.players) {
      if (p.left_at) continue;
      const mine = lastBets.filter((b) => b.player_id === p.id);
      const wanted: Record<string, number> = {};
      for (const b of mine) wanted[b.bet_type] = (wanted[b.bet_type] ?? 0) + b.bet_amount;
      const currentTotal = Object.values(wanted).reduce((a, b) => a + b, 0);
      if (currentTotal <= p.balance) p.bets = wanted;
    }
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.send(ws, "dealer_ok", { message: "Rebet applied" });
  }

  private async endGame(ws: WebSocket): Promise<void> {
    const g = this.state!;
    g.game_status = "finished";
    await this.save();
    this.broadcast("game_state", dealerStateView(g));
    this.systemChat("Game finished. Settle payouts with your players.");
    this.send(ws, "dealer_ok", { message: "Game finished" });
  }

  private async deleteGame(ws: WebSocket): Promise<void> {
    const g = this.state!;
    g.game_status = "deleted";
    await this.save();
    for (const [w, c] of this.conns) {
      if (c.role !== "dealer") this.send(w, "game_closed", { game_id: g.game_id });
    }
    this.state = null;
    this.send(ws, "games_list", { games: await this.listGameSummaries() });
  }

  // ================= player operations =================

  private playerOrFail(conn: Conn, ws: WebSocket): Player | null {
    if (conn.role !== "player" || !conn.playerId || !this.state) {
      this.err(ws, "not a player");
      return null;
    }
    const p = this.state.players.find((x) => x.id === conn.playerId && !x.left_at);
    if (!p) {
      this.err(ws, "you are not seated");
      return null;
    }
    return p;
  }

  private async placeBet(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    if (g.table_locked || g.spinning) return this.err(ws, "table is locked");
    if (g.game_status !== "active") return this.err(ws, "game not active");

    const target = String(d.target ?? "").trim().toLowerCase().slice(0, BET_TARGET_MAX_LEN);
    const amount = Math.trunc(Number(d.amount ?? 0));
    if (!isValidBetTarget(target)) return this.err(ws, "invalid bet target");
    if (!(amount > 0)) return this.err(ws, "invalid amount");

    const limit = p.is_vip
      ? (isInsideBet(target) ? g.max_bet_inner_vip : g.max_bet_outer_vip)
      : (isInsideBet(target) ? g.max_bet_inner : g.max_bet_outer);
    if (amount > limit) return this.err(ws, `max bet for this spot is ${limit}`);

    const staked = Object.values(p.bets).reduce((a, b) => a + b, 0);
    if (staked + amount > p.balance) return this.err(ws, "not enough balance");

    p.bets[target] = (p.bets[target] ?? 0) + amount;
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.send(ws, "bets_updated", { bets: p.bets, balance: p.balance });
  }

  private async removeBet(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    if (g.table_locked || g.spinning) return this.err(ws, "table is locked");
    const target = String(d.target ?? "").trim().toLowerCase();
    if (!(target in p.bets)) return this.err(ws, "no bet there");
    delete p.bets[target];
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.send(ws, "bets_updated", { bets: p.bets, balance: p.balance });
  }

  private async clearMyBets(conn: Conn, ws: WebSocket): Promise<void> {
    const g = this.state!;
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    if (g.table_locked || g.spinning) return this.err(ws, "table is locked");
    p.bets = {};
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.send(ws, "bets_updated", { bets: p.bets, balance: p.balance });
  }

  private async rebetMine(conn: Conn, ws: WebSocket): Promise<void> {
    const g = this.state!;
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    if (g.table_locked || g.spinning) return this.err(ws, "table is locked");
    const lastBets = g.all_bets.filter((b) => b.round_number === g.round_number && b.player_id === p.id);
    const wanted: Record<string, number> = {};
    for (const b of lastBets) wanted[b.bet_type] = (wanted[b.bet_type] ?? 0) + b.bet_amount;
    const total = Object.values(wanted).reduce((a, b) => a + b, 0);
    if (total > p.balance) return this.err(ws, "not enough balance to rebet");
    p.bets = wanted;
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.send(ws, "bets_updated", { bets: p.bets, balance: p.balance });
  }

  private async setReady(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    p.ready = !!d.ready;
    await this.save();
    this.broadcast("player_ready_updated", { player_id: p.id, ready: p.ready });
    this.broadcast("public_bets", { players: this.state!.players.map(publicPlayer) });
  }

  private async setAway(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    p.away = !!d.away;
    if (p.away) p.ready = false;
    await this.save();
    this.broadcast("player_away_updated", { player_id: p.id, away: p.away });
    this.broadcast("public_bets", { players: this.state!.players.map(publicPlayer) });
  }

  private async cashout(conn: Conn, ws: WebSocket): Promise<void> {
    const g = this.state!;
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    if (g.table_locked || g.spinning) return this.err(ws, "table is locked");
    if (Object.keys(p.bets).length > 0) return this.err(ws, "clear your bets before cashing out");

    p.cash_out_amount = p.balance;
    p.balance = 0;
    p.left_at = nowIso();
    g.game_status = g.players.every((x) => x.left_at) ? "payouts_complete" : g.game_status;
    await this.save();
    this.broadcast("public_bets", { players: g.players.map(publicPlayer) });
    this.broadcast("game_state", dealerStateView(g));
    this.systemChat(`${p.display_name} cashed out ${p.cash_out_amount.toLocaleString()} gil. See you next time!`);
    this.send(ws, "cashout_ok", { amount: p.cash_out_amount });
  }

  private async playerChat(conn: Conn, ws: WebSocket, d: any): Promise<void> {
    const g = this.state!;
    const p = this.playerOrFail(conn, ws);
    if (!p) return;
    const now = Date.now();
    if (now - conn.lastChatTs < 2000) return this.err(ws, "chat rate limited");
    conn.lastChatTs = now;
    const text = String(d.text ?? "").trim().slice(0, 300);
    if (text.length === 0) return;
    const msg: ChatMsg = {
      id: cryptoRandomHex(5),
      from_id: p.id,
      from_name: p.display_name,
      text,
      ts: nowIso(),
      role: "player",
      color: p.color,
    };
    g.chat.push(msg);
    if (g.chat.length > MAX_CHAT) g.chat.splice(0, g.chat.length - MAX_CHAT);
    await this.save();
    this.broadcast("chat_message", msg);
  }

  // ---------- ledger ----------

  private ledgerView() {
    const g = this.state;
    if (!g) return { players: [] };
    return {
      game_info: {
        game_id: g.game_id,
        spin_mode: g.spin_mode,
        control_number: g.control_number,
        created_at: g.created_at,
        final_status: g.game_status,
        dealer_name: g.dealer_name,
        dealer_homeworld: g.dealer_world,
        total_players: g.players.length,
        total_rounds: g.round_number,
      },
      summary: {
        total_buy_ins: g.adjustments.filter((a) => a.adjustment_amount > 0).reduce((s, a) => s + a.adjustment_amount, 0),
        total_cash_outs: g.players.reduce((s, p) => s + (p.cash_out_amount ?? 0), 0),
        total_adjustments: g.adjustments.reduce((s, a) => s + a.adjustment_amount, 0),
        house_edge: 0,
        total_tips: g.total_tips,
      },
      players: g.players.map((p) => ({
        player_id: p.id,
        player_name: p.name,
        color: p.color,
        joined_at: p.joined_at,
        left_at: p.left_at,
        buy_in_amount: p.buy_in_total,
        final_balance: p.balance,
        cash_out_amount: p.cash_out_amount,
      })),
      rounds: g.rounds,
      all_bets: g.all_bets,
      adjustments: g.adjustments,
    };
  }
}

// ---------- helpers ----------

function clampNum(v: any, min: number, max: number, fallback: number): number {
  const n = Math.trunc(Number(v));
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

function dealerStateView(g: GameState) {
  return {
    spinning: g.spinning,
    table_locked: g.table_locked,
    awaiting_dice: g.awaiting_dice,
    spin_mode: g.spin_mode,
    control_number: g.control_number,
    creator_name: g.dealer_name,
    creator_homeworld: g.dealer_world,
    game_status: g.game_status,
    max_bet_inner: g.max_bet_inner,
    max_bet_outer: g.max_bet_outer,
    max_bet_inner_vip: g.max_bet_inner_vip,
    max_bet_outer_vip: g.max_bet_outer_vip,
    chip_denominations: g.chip_denominations,
    players: g.players.map(publicPlayer),
  };
}
