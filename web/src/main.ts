import { Api, colorOf, displayNumber, GameStateMsg, HistoryEntry, MeState, PublicBetsMsg, PublicPlayer, Role } from "./protocol";
import { Wheel } from "./wheel";
import { Sfx } from "./audio";
import { Board, type BetChip } from "./board";

// ---------- settings ----------

const DEFAULT_API_URL = "wss://private-roulette-api.dexcss-roulette.workers.dev/ws";
const SETTINGS_KEY = "pr_settings_v1";

interface Settings {
  apiUrl: string;
}

function loadSettings(): Settings {
  try {
    return { apiUrl: DEFAULT_API_URL, ...JSON.parse(localStorage.getItem(SETTINGS_KEY) ?? "{}") };
  } catch {
    return { apiUrl: DEFAULT_API_URL };
  }
}

function saveSettings(s: Settings) {
  localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
}

const settings = loadSettings();
const sfx = new Sfx();

// ---------- app state ----------

const api = new Api(settings.apiUrl);
let role: Role = "spectator";
let gameId = "";
let playerKey = "";
let me: MeState | null = null;
let state: GameStateMsg | null = null;
let lastHistory: HistoryEntry[] = [];
let wheel: Wheel | null = null;
let board: Board | null = null;
let selectedChip = 10000;
let rejoinBets: Record<string, number> | null = null;
let prevBalance = 0;
let pendingRound: any = null;
let lastPlayers: PublicPlayer[] = [];
type PendingBet =
  | { kind: "place"; target: string; amount: number; expected: number }
  | { kind: "remove"; target: string };
let serverMyBets: Record<string, number> = {};
let pendingBets: PendingBet[] = [];
let renderQueued = false;
let dealerToken = localStorage.getItem("pr_dealer_token") ?? "";
let dealerGames: any[] = [];
let lastLedger: any = null;
const playerKeys = new Map<string, string>();
const shownBalances = new Map<string, number>();

// ---------- dom ----------

const $ = <T extends HTMLElement = HTMLElement>(sel: string) => document.querySelector<T>(sel)!;

const viewLanding = $("#view-landing");
const viewTable = $("#view-table");
const viewLocked = $("#view-locked");
const chipSelect = document.getElementById("chips")!;
const boardEl = $("#board");
const wheelCanvas = $("#wheel") as HTMLCanvasElement;
const historyStrip = $("#history");
const playersList = $("#players");
const chatLog = $("#chat-log");
const chatInput = $("#chat-input") as HTMLInputElement;
const balanceLabel = $("#balance");
const tableInfo = $("#table-info");
const statusLabel = $("#status");
const readyBtn = $("#ready-btn") as HTMLButtonElement;
const cashoutBtn = $("#cashout-btn") as HTMLButtonElement;
const rebetBtn = $("#rebet-btn") as HTMLButtonElement;
const clearBtn = $("#clear-btn") as HTMLButtonElement;
const controlNote = $("#control-note");
const lastNet = $("#last-net");
const proofLine = $("#proof-line");
const settingsDlg = $("#settings-dlg") as HTMLDialogElement;
const dealerCard = $("#dealer-card");
const dealerTokenInput = $("#dealer-token");
const dealerGamesEl = $("#dealer-games");
const dealerBar = $("#dealer-bar");

// ---------- landing ----------

function parseHash(): { gameId: string; playerKey: string; spectate: boolean; dealer?: boolean; dealerToken?: string } | null {
  // #/{gameId}/{playerKey}  |  #/spectate/{gameId}  |  #/dealer/{gameId}/{token}
  const h = location.hash.replace(/^#\/?/, "");
  if (!h) return null;
  const parts = h.split("/").map(decodeURIComponent);
  if (parts[0] === "dealer") {
    return {
      gameId: parts[1] && parts[1] !== "-" ? parts[1] : "",
      playerKey: "",
      spectate: false,
      dealer: true,
      dealerToken: parts[2] || undefined,
    };
  }
  if (parts[0] === "spectate" && parts[1]) return { gameId: parts[1], playerKey: "", spectate: true };
  if (parts.length >= 2) return { gameId: parts[0], playerKey: parts[1], spectate: false };
  return null;
}

function tryAutoJoin() {
  const link = parseHash();
  const gid = link?.gameId || ($("#join-game") as HTMLInputElement).value.trim();
  const pkey = link?.playerKey || ($("#join-key") as HTMLInputElement).value.trim();
  if (!gid) return;
  role = link?.spectate || (!pkey && !($("#join-key") as HTMLInputElement).value.trim()) ? "spectator" : "player";
  gameId = gid;
  playerKey = pkey;
  api.connect();
  showConnecting();
}

$("#join-btn").addEventListener("click", tryAutoJoin);
$("#spectate-btn").addEventListener("click", () => {
  const gid = ($("#join-game") as HTMLInputElement).value.trim();
  if (!gid) return;
  role = "spectator";
  gameId = gid;
  playerKey = "";
  api.connect();
  showConnecting();
});
$("#open-settings").addEventListener("click", () => {
  ($("#api-url-input") as HTMLInputElement).value = settings.apiUrl;
  settingsDlg.showModal();
});
$("#settings-save").addEventListener("click", (e) => {
  e.preventDefault();
  settings.apiUrl = (($("#api-url-input") as HTMLInputElement).value || DEFAULT_API_URL).trim();
  saveSettings(settings);
  api.setUrl(settings.apiUrl);
  settingsDlg.close();
});

function showConnecting() {
  viewLanding.hidden = true;
  viewLocked.hidden = true;
  viewTable.hidden = false;
  wheel?.refreshSize();
  statusLabel.textContent = "connecting…";
}

// ---------- lifecycle ----------

api.onOpen(() => {
  statusLabel.textContent = "connected";
  api.send("hello", role === "dealer"
    ? { role, token: localStorage.getItem("pr_dealer_token") ?? "", dealer_name: "Web Dealer", dealer_world: "" }
    : { role, game_id: gameId, player_key: playerKey });
  if (role === "player") api.send("set_ready", { ready: false });
});

api.onClose(() => {
  pendingBets = [];
  queueRender();
  statusLabel.textContent = "disconnected — retrying…";
});

api.on("hello_ok", (d) => {
  if (d.role === "player") {
    me = { playerId: d.player_id, displayName: d.display_name };
    // players may re-open the page: request nothing special; state arrives
  }
  if (d.role === "dealer") {
    role = "dealer";
    dealerCard.hidden = false;
    (dealerTokenInput as HTMLInputElement).value = dealerToken;
    statusLabel.textContent = "dealer connected";
    if (gameId) {
      openDealerTable();
    } else {
      viewLanding.hidden = false;
      viewTable.hidden = true;
    }
  }
});

api.on("games_list", (d: { games: any[] }) => {
  dealerGames = d.games ?? [];
  renderDealerGames();
});

api.on("game_created", (d: { game_id: string }) => {
  if (role !== "dealer") return;
  gameId = d.game_id;
  openDealerTable();
});

api.on("dealer_ok", (d: { message?: string }) => {
  statusLabel.textContent = d.message ?? "ok";
});

api.on("ledger", (d: any) => {
  lastLedger = d;
  statusLabel.textContent = `ledger: ${(d.rounds ?? []).length} rounds · ${(d.all_bets ?? []).length} bets`;
});

api.on("game_state", (d: GameStateMsg) => {
  state = d;
  for (const p of ((d as any).players ?? []) as any[]) {
    if (p?.id && p?.player_key) playerKeys.set(p.id, p.player_key);
  }
  lastPlayers = d.players ?? [];
  const mine = me ? lastPlayers.find((p) => p.id === me!.playerId) : undefined;
  if (mine) {
    serverMyBets = mine.bets ?? {};
    pendingBets = [];
  }
  queueRender();
  renderState();
});

api.on("public_bets", (d: PublicBetsMsg) => {
  lastPlayers = d.players;
  const mine = me ? d.players.find((p) => p.id === me!.playerId) : undefined;
  if (mine) {
    serverMyBets = mine.bets ?? {};
    reconcilePending(serverMyBets);
  }
  queueRender();
});

api.on("table_history", (d: { history: HistoryEntry[] }) => {
  lastHistory = d.history ?? [];
  renderHistory();
  renderStats();
});

api.on("chat_history", (d: { messages: any[] }) => {
  chatLog.innerHTML = "";
  deferredChat.length = 0;
  d.messages.forEach(addChatLine);
  scrollChat();
});

const deferredChat: any[] = [];
api.on("chat_message", (m: any) => {
  if (wheel?.isSpinning || pendingRound || state?.spinning) deferredChat.push(m);
  else addChatLine(m);
});

api.on("spin", (d) => {
  if (!wheel) return;
  wheel.spinTo(d.result);
  renderProof(d);
  renderHistoryFromSpin(d.result);
});

api.on("round_result", (d) => {
  pendingRound = d;
  if (!wheel?.isSpinning) revealRound();
});

function revealRound(): void {
  if (!pendingRound) return;
  const round = pendingRound;
  pendingRound = null;
  const myId = me?.playerId;
  if (myId) {
    const newBal = round.balances?.[myId];
    if (newBal !== undefined) {
      const delta = newBal - prevBalance;
      if (delta !== 0) {
        flashResult(delta > 0 ? `+${delta.toLocaleString()}` : `−${Math.abs(delta).toLocaleString()}`, delta > 0);
        if (delta > 0) sfx.win(); else sfx.lose();
      }
      prevBalance = newBal;
    }
  }
  renderBalance(lastPlayers, true);
  renderPlayers(lastPlayers);
  for (const m of deferredChat.splice(0)) addChatLine(m);
}

api.on("bets_updated", (d: { bets: Record<string, number> }) => {
  if (!me) return;
  serverMyBets = d.bets ?? {};
  reconcilePending(serverMyBets);
  queueRender();
});

api.on("cashout_ok", () => {
  statusLabel.textContent = "cashed out — thanks for playing!";
});

api.on("dealer_error", (d) => {
  sfx.error();
  if (pendingBets.length > 0) {
    pendingBets = [];
    queueRender();
  }
  statusLabel.textContent = d.message ?? "error";
  setTimeout(() => {
    if (statusLabel.textContent === d.message) statusLabel.textContent = "";
  }, 4000);
});

api.on("game_closed", () => {
  location.hash = "";
  location.reload();
});

// ---------- rendering ----------

function renderState() {
  if (!state) return;
  tableInfo.innerHTML = `
    <strong>${escapeHtml(state.creator_name)}${state.creator_homeworld ? "@" + escapeHtml(state.creator_homeworld) : ""}</strong>
    <span class="muted">· game ${escapeHtml(gameId)}</span>
    <span class="muted">· ${state.spin_mode} spin</span>
    ${state.control_number ? `<span class="muted">· control #${escapeHtml(state.control_number)}</span>` : ""}
    <span class="pill ${state.game_status}">${state.game_status}</span>`;

  if (state.spin_mode === "control") {
    controlNote.textContent = `Provable fairness — control #${state.control_number}. Formula: sha256("<dice>:<control>") → first 8 hex as uint32 → % 38.`;
    controlNote.hidden = false;
  } else {
    controlNote.hidden = true;
  }

  // locked / awaiting state
  const waiting = state.table_locked || state.spinning;
  // Only players can bet, so only they get the blocking overlay; spectators
  // keep watching the board while the wheel spins.
  $("#betting-blocker").hidden = !waiting || role !== "player";
  $("#lock-label").textContent = state.spinning
    ? "Wheel is spinning…"
    : state.awaiting_dice
      ? "Waiting for the dealer's dice roll…"
      : "Table locked";

  if (board && state.chip_denominations?.length) {
    renderChips(state.chip_denominations);
  }
    const limits = document.getElementById("limits");
  if (limits) {
    const total = state.max_bet_total ?? 0;
    const totalVip = state.max_bet_total_vip ?? 0;
    limits.textContent =
      `max per spot: inside ${state.max_bet_inner.toLocaleString()} · outside ${state.max_bet_outer.toLocaleString()}` +
      ` · max total: ${total > 0 ? total.toLocaleString() : "off"}` +
      ` · VIP: ${state.max_bet_inner_vip.toLocaleString()}/${state.max_bet_outer_vip.toLocaleString()}/${totalVip > 0 ? totalVip.toLocaleString() : "off"}`;
  }
  if (role === "spectator") statusLabel.textContent = "spectating";
  boardEl.classList.toggle("readonly", role !== "player");

  // dealer controls
  dealerBar.hidden = role !== "dealer";
  if (role === "dealer") {
    const manualWrap = document.getElementById("d-manual-wrap");
    if (manualWrap) manualWrap.hidden = state.spin_mode !== "manual";
    const spinBtn = document.getElementById("d-spin") as HTMLButtonElement | null;
    const diceInput = document.getElementById("d-dice") as HTMLInputElement | null;
    if (spinBtn && diceInput) {
      // the dice spin is only possible while the table is locked and in control mode
      const armed = state.spin_mode === "control" && state.table_locked && !state.spinning;
      spinBtn.disabled = !armed;
      diceInput.disabled = !armed;
    }
    const reqBtn = document.getElementById("d-spin-req") as HTMLButtonElement | null;
    if (reqBtn) reqBtn.disabled = state.table_locked || state.spinning;
  }

  // player controls
  if (role === "player" && me) {
    const myId = me.playerId;
    const mine = state.players.find((p) => p.id === myId);
    if (mine) {
      readyBtn.textContent = mine.ready ? "✓ Ready" : "Ready up";
      readyBtn.classList.toggle("on", mine.ready);
      const left = mine.balance === 0 && mine.bets && Object.keys(mine.bets).length === 0;
      cashoutBtn.disabled = left;
    }
  }
  readyBtn.hidden = role !== "player";
  cashoutBtn.hidden = role !== "player";
  rebetBtn.hidden = role !== "player";
  clearBtn.hidden = role !== "player";

  // The table/board stays visible for everyone - spectators and the dealer need
  // to follow the action. Only the betting controls are player-only.
  $("#bet-wrap").hidden = false;
  $("#chips").hidden = role !== "player";
  const actions = document.querySelector("#bet-wrap .actions") as HTMLElement | null;
  if (actions) actions.hidden = role !== "player";
  const hint = document.querySelector(".board-hint") as HTMLElement | null;
  if (hint) hint.hidden = role !== "player";
}

function betChips(players: PublicPlayer[]): BetChip[] {
  const out: BetChip[] = [];
  for (const p of players) {
    for (const [target, amount] of Object.entries(p.bets ?? {})) {
      if (amount > 0) {
        out.push({
          target,
          amount,
          color: p.color,
          playerId: p.id,
          name: p.display_name,
          mine: !!me && p.id === me.playerId,
        });
      }
    }
  }
  return out;
}

function mergedMyBets(): Record<string, number> {
  const out = { ...serverMyBets };
  for (const op of pendingBets) {
    if (op.kind === "place") out[op.target] = (out[op.target] ?? 0) + op.amount;
    else delete out[op.target];
  }
  return out;
}

/** The server's player list with this player's optimistic bets overlaid. */
function withMyBets(players: PublicPlayer[]): PublicPlayer[] {
  if (!me) return players;
  const merged = mergedMyBets();
  return players.map((p) => (p.id === me!.playerId ? { ...p, bets: merged } : p));
}

/** Drop optimistic operations the server has already reflected. */
function reconcilePending(server: Record<string, number>): void {
  pendingBets = pendingBets.filter((op) => {
    if (op.kind === "place") return (server[op.target] ?? 0) < op.expected;
    return op.target in server;
  });
}

/** Coalesce all board/list/balance updates into one render per frame. */
function queueRender(): void {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => {
    renderQueued = false;
    const players = withMyBets(lastPlayers);
    board?.renderBets(betChips(players));
    renderPlayers(players);
    renderBalance(players);
  });
}

function renderBalance(players: PublicPlayer[], force = false) {
  if (!me) return;
  // Hold the new balance and win/loss flash until the wheel lands.
  if (!force && (wheel?.isSpinning || pendingRound || state?.spinning)) return;
  const mine = players.find((p) => p.id === me!.playerId);
  if (mine) {
    const staked = Object.values(mine.bets ?? {}).reduce((a, b) => a + b, 0);
    const left = mine.balance - staked;
    balanceLabel.textContent = `${mine.balance.toLocaleString()} gil · ${left.toLocaleString()} left`;
    const limitsEl = document.getElementById("limits");
    if (limitsEl && state) {
      const total = state.max_bet_total ?? 0;
      limitsEl.textContent =
        `max per spot ${state.max_bet_inner.toLocaleString()} · max total ${total > 0 ? total.toLocaleString() : "off"}` +
        ` · staked ${staked.toLocaleString()}`;
    }
    prevBalance = mine.balance;
    lastNet.textContent = mine.last_round_net === 0 ? "" : mine.last_round_net > 0 ? `▲ ${mine.last_round_net.toLocaleString()}` : `▼ ${Math.abs(mine.last_round_net).toLocaleString()}`;
    lastNet.className = mine.last_round_net >= 0 ? "net pos" : "net neg";
  }
}

function renderPlayers(players: PublicPlayer[]) {
  const dealerActions = (p: PublicPlayer) =>
    role === "dealer"
      ? `<span class="pactions">
           <button data-act="plus" data-id="${p.id}" title="+100k">+100k</button>
           <button data-act="minus" data-id="${p.id}" title="-100k">-100k</button>
           <button data-act="cashout" data-id="${p.id}" title="Settle and remove">Cash out</button>
           <button data-act="link" data-id="${p.id}" title="Copy invite link">Link</button>
         </span>`
      : "";

    playersList.innerHTML = players
    .map((p) => {
      // Freeze displayed balances while the wheel is spinning; reveal on landing.
      const frozen = !!wheel?.isSpinning || !!pendingRound || !!state?.spinning;
      const bal = frozen ? (shownBalances.get(p.id) ?? p.balance) : p.balance;
      if (!frozen) shownBalances.set(p.id, p.balance);
      return `
      <div class="player ${p.away ? "away" : ""}" style="border-color:${p.color}66">
        <span class="dot" style="background:${p.color}"></span>
        <span class="pname">${escapeHtml(p.display_name)}</span>
        ${p.ready ? '<span class="tag ready">ready</span>' : ""}
        ${p.is_vip ? '<span class="tag vip">vip</span>' : ""}
        <span class="pbets">${Object.entries(p.bets).map(([t, a]) => `${a.toLocaleString()}`).join(" / ") || ""}</span>
        <span class="pbal">${bal.toLocaleString()}</span>
        ${dealerActions(p)}
      </div>`;
    })
    .join("");
}

function renderHistory() {
  historyStrip.innerHTML = lastHistory
    .slice(-14)
    .reverse()
    .map((h) => `<span class="hist ${h.color}">${displayNumber(h.result)}</span>`)
    .join("");
}

function renderHistoryFromSpin(result: number) {
  lastHistory = [...lastHistory, { ts: new Date().toISOString(), result, color: colorOf(result) }];
  renderHistory();
  renderStats();
}

function renderChips(denoms: number[]) {
  chipSelect.innerHTML = "";
  for (const v of denoms) {
    const b = document.createElement("button");
    b.className = "chip" + (v === selectedChip ? " sel" : "");
    b.textContent = formatChip(v);
    b.addEventListener("click", () => {
      selectedChip = v;
      chipSelect.querySelectorAll(".chip").forEach((c) => c.classList.remove("sel"));
      b.classList.add("sel");
    });
    chipSelect.appendChild(b);
  }
}

function renderProof(spin: any) {
  if (!spin?.proof || spin.proof.hash === "manual") {
    proofLine.textContent = spin?.proof?.hash === "manual" ? "manual spin (dealer's choice)" : "";
    return;
  }
  proofLine.textContent = `proof: dice ${spin.proof.dice_roll} · sha256("${spin.proof.dice_roll}:${spin.proof.control_number}")[0..8] = ${spin.proof.seed} → ${spin.proof.seed} % 38 = ${displayNumber(spin.result)} ✓`;
}

function addChatLine(m: any) {
  const div = document.createElement("div");
  div.className = `chatline ${m.role}`;
  div.innerHTML = `<span class="cname" style="color:${m.color}">${escapeHtml(m.from_name)}</span> ${escapeHtml(m.text)}`;
  chatLog.appendChild(div);
  while (chatLog.children.length > 100) chatLog.removeChild(chatLog.firstChild!);
  scrollChat();
}

function scrollChat() {
  chatLog.scrollTop = chatLog.scrollHeight;
}

// ---------- wiring ----------

$("#chat-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const text = chatInput.value.trim();
  if (!text) return;
  if (role === "player") api.send("send_player_chat", { text });
  chatInput.value = "";
});

// mute toggle
const muteBtn = $("#mute-btn");
if (muteBtn) {
  muteBtn.textContent = sfx.muted ? "🔇" : "🔊";
  muteBtn.addEventListener("click", () => {
    sfx.setMuted(!sfx.muted);
    muteBtn.textContent = sfx.muted ? "🔇" : "🔊";
    if (!sfx.muted) sfx.ready();
  });
}

readyBtn.addEventListener("click", () => {
  if (!state || !me) return;
  sfx.ready();
  const myId = me.playerId;
  const mine = state.players.find((p) => p.id === myId);
  api.send("set_ready", { ready: !mine?.ready });
});

cashoutBtn.addEventListener("click", () => {
  if (confirm("Cash out your full balance? The dealer will trade your winnings.")) {
    api.send("cashout");
  }
});

rebetBtn.addEventListener("click", () => api.send("rebet_mine"));
clearBtn.addEventListener("click", () => api.send("clear_my_bets"));

// ---------- dealer console ----------

function renderDealerGames(): void {
  dealerGamesEl.innerHTML = dealerGames.length
    ? dealerGames
        .map(
          (g) => `<div class="dg">
            <span class="dgid">${g.id}</span>
            <span class="muted small">${g.status} · ${g.players}p${g.control_number ? " · #" + g.control_number : ""}</span>
            <button data-open="${g.id}">Open</button>
            <button data-del="${g.id}" class="ghost" title="Shift+click to delete">✕</button>
          </div>`,
        )
        .join("")
    : `<div class="muted small">no games yet — create one below</div>`;
}

function openDealerTable(): void {
  viewLanding.hidden = true;
  viewTable.hidden = false;
  wheel?.refreshSize();
  dealerBar.hidden = false;
  api.send("join_game", { game_id: gameId });
}

$("#dealer-open").addEventListener("click", (e) => {
  e.preventDefault();
  dealerCard.hidden = false;
  (dealerTokenInput as HTMLInputElement).value = dealerToken;
});

$("#dealer-login").addEventListener("click", () => {
  const token = (dealerTokenInput as HTMLInputElement).value.trim();
  if (!token) return;
  dealerToken = token;
  localStorage.setItem("pr_dealer_token", token);
  role = "dealer";
  api.disconnect();
  api.connect();
});

$("#dealer-logout").addEventListener("click", () => {
  localStorage.removeItem("pr_dealer_token");
  location.reload();
});

$("#dealer-create").addEventListener("click", () => {
  api.send("create_game", {
    spin_mode: "control",
    dealer_name: "Web Dealer",
    dealer_world: "",
    max_bet_inner: 500000,
    max_bet_outer: 250000,
    max_bet_inner_vip: 1000000,
    max_bet_outer_vip: 500000,
    max_bet_total: 500000,
    max_bet_total_vip: 1000000,
  });
});

dealerGamesEl.addEventListener("click", (e) => {
  const t = e.target as HTMLElement;
  const open = t.getAttribute("data-open");
  const del = t.getAttribute("data-del");
  if (open) {
    gameId = open;
    openDealerTable();
  } else if (del && e.shiftKey) {
    api.send("join_game", { game_id: del });
    setTimeout(() => api.send("delete_game", { game_id: del }), 300);
  }
});

$("#d-games").addEventListener("click", () => {
  viewTable.hidden = true;
  viewLanding.hidden = false;
  dealerCard.hidden = false;
  gameId = "";
  api.send("list_games");
});

$("#d-add").addEventListener("click", () => {
  const name = ($("#d-name") as HTMLInputElement).value.trim();
  if (!name || !gameId) return;
  const bankroll = Number(($("#d-bankroll") as HTMLInputElement).value) || 0;
  api.send("add_player", {
    name,
    display_name: name.split("@")[0],
    bankroll,
    game_id: gameId,
    is_vip: ($("#d-vip") as HTMLInputElement).checked,
    is_freeplay: ($("#d-free") as HTMLInputElement).checked,
  });
  ($("#d-name") as HTMLInputElement).value = "";
});

$("#d-spin-req").addEventListener("click", () => {
  api.send("request_spin", { game_id: gameId });
  sfx.ready();
});

$("#d-spin").addEventListener("click", () => {
  const dice = Number(($("#d-dice") as HTMLInputElement).value);
  if (!Number.isFinite(dice) || dice < 0 || dice > 999) {
    statusLabel.textContent = "dice must be 0-999";
    return;
  }
  api.send("spin", { game_id: gameId, dice_roll: dice });
});

$("#d-force").addEventListener("click", () => {
  const n = Number(($("#d-manual") as HTMLInputElement).value);
  if (!Number.isFinite(n) || n < 0 || n > 37) {
    statusLabel.textContent = "number must be 0-36 or 37 (00)";
    return;
  }
  api.send("spin", { game_id: gameId, number: n });
});

$("#d-clear").addEventListener("click", () => api.send("clear_bets", { game_id: gameId }));
$("#d-rebet").addEventListener("click", () => api.send("rebet", { game_id: gameId }));
$("#d-ledger").addEventListener("click", () => api.send("get_ledger", { game_id: gameId }));

$("#d-csv").addEventListener("click", () => {
  if (!lastLedger) {
    statusLabel.textContent = "refresh the ledger first";
    return;
  }
  const rows: string[] = ["section,round,player,detail,amount,payout,time"];
  for (const r of lastLedger.rounds ?? []) {
    rows.push(`round,${r.round_number},,winning ${displayNumber(r.winning_number)} ${r.winning_color},${r.total_bets},${r.total_payouts},${r.spun_at}`);
  }
  for (const b of lastLedger.all_bets ?? []) {
    rows.push(`bet,${b.round_number},"${b.player_name}",${b.bet_type},${b.bet_amount},${b.payout_amount},${b.placed_at}`);
  }
  for (const p of lastLedger.players ?? []) {
    rows.push(`player,,"${p.player_name}",,${p.buy_in_amount},${p.cash_out_amount ?? ""},${p.joined_at}`);
  }
  for (const a of lastLedger.adjustments ?? []) {
    rows.push(`adjustment,,"${a.player_name}","${a.reason}",${a.adjustment_amount},,${a.adjusted_at}`);
  }
  const blob = new Blob([rows.join("\n")], { type: "text/csv" });
  const a = document.createElement("a");
  a.href = URL.createObjectURL(blob);
  a.download = `${gameId || "ledger"}.csv`;
  a.click();
  URL.revokeObjectURL(a.href);
});

playersList.addEventListener("click", (e) => {
  if (role !== "dealer") return;
  const t = e.target as HTMLElement;
  const act = t.getAttribute("data-act");
  const id = t.getAttribute("data-id");
  if (!act || !id || !gameId) return;
  if (act === "plus") api.send("adjust_balance", { player_id: id, delta: 100000, game_id: gameId });
  if (act === "minus") api.send("adjust_balance", { player_id: id, delta: -100000, game_id: gameId });
  if (act === "cashout") api.send("cashout_player", { player_id: id, game_id: gameId });
  if (act === "link") {
    const key = playerKeys.get(id);
    if (key) {
      const url = `${location.origin}${location.pathname}#/${gameId}/${key}`;
      navigator.clipboard?.writeText(url);
      statusLabel.textContent = "invite link copied";
    } else {
      statusLabel.textContent = "open the game state first (player key not loaded)";
    }
  }
});


// ---------- boot ----------

wheel = new Wheel(wheelCanvas);
wheel.setTickCallback(() => sfx.tick());
wheel.setSettleCallback(() => { sfx.land(); revealRound(); });
board = new Board(boardEl, {
  place: (target) => {
    if (role !== "player" || !me) return;
    sfx.chip();
    const base = mergedMyBets()[target] ?? 0;
    pendingBets.push({ kind: "place", target, amount: selectedChip, expected: base + selectedChip });
    queueRender();
    api.send("place_bet", { target, amount: selectedChip });
  },
  remove: (target) => {
    if (role !== "player" || !me) return;
    pendingBets.push({ kind: "remove", target });
    queueRender();
    api.send("remove_bet", { target });
  },
});

const auto = parseHash();
if (auto) {
  if (auto.dealer) {
    role = "dealer";
    gameId = auto.gameId ?? "";
    playerKey = "";
    if (auto.dealerToken) {
      // Auto-login from the plugin button, then remove the secret from the URL.
      dealerToken = auto.dealerToken;
      localStorage.setItem("pr_dealer_token", dealerToken);
      const clean = `#/dealer/${gameId || ""}`.replace(/\/$/, "");
      history.replaceState(null, "", `${location.pathname}${location.search}${clean}`);
    }
    if (gameId) ($("#join-game") as HTMLInputElement).value = gameId;
    if (dealerToken) {
      api.connect();
      if (gameId) showConnecting();
      else { viewLanding.hidden = false; dealerCard.hidden = false; }
    } else {
      viewLanding.hidden = false;
      dealerCard.hidden = false;
      statusLabel.textContent = "enter your dealer token to open this table";
    }
  } else {
    role = auto.spectate ? "spectator" : "player";
    gameId = auto.gameId;
    playerKey = auto.playerKey;
    ($("#join-game") as HTMLInputElement).value = auto.gameId;
    (  $("#join-key") as HTMLInputElement).value = auto.playerKey;
    tryAutoJoin();
  }
}

function flashResult(text: string, positive: boolean): void {
  const el = document.getElementById("result-flash");
  if (!el) return;
  el.textContent = text;
  el.className = "result-flash " + (positive ? "pos" : "neg");
  void el.offsetWidth; // restart animation
  el.classList.add("show");
  setTimeout(() => el.classList.remove("show"), 2200);
}

function renderStats(): void {
  const el = document.getElementById("stats-bar");
  if (!el) return;
  const recent = lastHistory.slice(-24);
  if (recent.length === 0) {
    el.innerHTML = "";
    return;
  }
  let red = 0, black = 0, green = 0, odd = 0, even = 0;
  const counts = new Map<number, number>();
  for (const h of recent) {
    if (h.color === "red") red++;
    else if (h.color === "black") black++;
    else green++;
    if (h.result !== 0 && h.result !== 37) {
      if (h.result % 2 === 1) odd++; else even++;
    }
    counts.set(h.result, (counts.get(h.result) ?? 0) + 1);
  }
  const hot = [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 3)
    .map(([n, c]) => `${n}(${c})`).join(" ");
  el.innerHTML =
    `<span class="sc red">R ${red}</span><span class="sc black">B ${black}</span><span class="sc green">G ${green}</span>` +
    `<span class="sc">ODD ${odd}</span><span class="sc">EVEN ${even}</span>` +
    `<span class="sc hot">HOT ${hot || "—"}</span>`;
}
function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
}

function formatChip(v: number): string {
  if (v >= 1_000_000) return `${v / 1_000_000}m`;
  if (v >= 1000) return `${v / 1000}k`;
  return String(v);
}
