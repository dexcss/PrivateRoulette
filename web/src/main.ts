import { Api, colorOf, displayNumber, GameStateMsg, HistoryEntry, MeState, PublicBetsMsg, PublicPlayer, Role } from "./protocol";
import { Wheel } from "./wheel";
import { Sfx } from "./audio";
import { Board } from "./board";

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
let selectedChip = 100;
let rejoinBets: Record<string, number> | null = null;
let prevBalance = 0;

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

// ---------- landing ----------

function parseHash(): { gameId: string; playerKey: string; spectate: boolean } | null {
  // #/{gameId}/{playerKey}  or  #/spectate/{gameId}
  const h = location.hash.replace(/^#\/?/, "");
  if (!h) return null;
  const parts = h.split("/").map(decodeURIComponent);
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
  statusLabel.textContent = "connecting…";
}

// ---------- lifecycle ----------

api.onOpen(() => {
  statusLabel.textContent = "connected";
  api.send("hello", role === "dealer" ? { role, token: localStorage.getItem("pr_dealer_token") ?? "" } : { role, game_id: gameId, player_key: playerKey });
  if (role === "player") api.send("set_ready", { ready: false });
});

api.onClose(() => {
  statusLabel.textContent = "disconnected — retrying…";
});

api.on("hello_ok", (d) => {
  if (d.role === "player") {
    me = { playerId: d.player_id, displayName: d.display_name };
    // players may re-open the page: request nothing special; state arrives
  }
});

api.on("game_state", (d: GameStateMsg) => {
  state = d;
  renderState();
});

api.on("public_bets", (d: PublicBetsMsg) => {
  renderPlayers(d.players);
  if (board && me) {
    const myId = me.playerId;
    const mine = d.players.find((p) => p.id === myId);
    if (mine) board.renderBets(mine.bets);
    renderBalance(d.players);
  }
});

api.on("table_history", (d: { history: HistoryEntry[] }) => {
  lastHistory = d.history ?? [];
  renderHistory();
  renderStats();
});

api.on("chat_history", (d: { messages: any[] }) => {
  chatLog.innerHTML = "";
  d.messages.forEach(addChatLine);
  scrollChat();
});

api.on("chat_message", addChatLine);

api.on("spin", (d) => {
  if (!wheel) return;
  wheel.spinTo(d.result, 6500);
  renderProof(d);
  renderHistoryFromSpin(d.result);
});

api.on("round_result", (d) => {
  // balance delta flash + sound
  const myId = me?.playerId;
  if (myId) {
    const newBal = d.balances?.[myId];
    if (newBal !== undefined) {
      const delta = newBal - prevBalance;
      if (delta !== 0) {
        flashResult(delta > 0 ? `+${delta.toLocaleString()}` : `−${Math.abs(delta).toLocaleString()}`, delta > 0);
        if (delta > 0) sfx.win(); else sfx.lose();
      }
      prevBalance = newBal;
    }
  }
});

api.on("bets_updated", (d) => {
  if (board) board.renderBets(d.bets ?? {});
});

api.on("cashout_ok", () => {
  statusLabel.textContent = "cashed out — thanks for playing!";
});

api.on("dealer_error", (d) => {
  sfx.error();
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
  $("#betting-blocker").hidden = !waiting;
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
    limits.textContent =
      `max inside ${state.max_bet_inner.toLocaleString()} · outside ${state.max_bet_outer.toLocaleString()}` +
      ` · VIP ${state.max_bet_inner_vip.toLocaleString()}/${state.max_bet_outer_vip.toLocaleString()}`;
  }
  if (role === "spectator") statusLabel.textContent = "spectating";

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
  $("#bet-wrap").hidden = role !== "player";
  $("#chips").hidden = role !== "player";
}

function renderBalance(players: PublicPlayer[]) {
  if (!me) return;
  const mine = players.find((p) => p.id === me!.playerId);
  if (mine) {
    balanceLabel.textContent = `${mine.balance.toLocaleString()} gil`;
    prevBalance = mine.balance;
    lastNet.textContent = mine.last_round_net === 0 ? "" : mine.last_round_net > 0 ? `▲ ${mine.last_round_net.toLocaleString()}` : `▼ ${Math.abs(mine.last_round_net).toLocaleString()}`;
    lastNet.className = mine.last_round_net >= 0 ? "net pos" : "net neg";
  }
}

function renderPlayers(players: PublicPlayer[]) {
  playersList.innerHTML = players
    .map(
      (p) => `
      <div class="player ${p.away ? "away" : ""}">
        <span class="dot" style="background:${p.color}"></span>
        <span class="pname">${escapeHtml(p.display_name)}</span>
        ${p.ready ? '<span class="tag ready">ready</span>' : ""}
        ${p.is_vip ? '<span class="tag vip">vip</span>' : ""}
        <span class="pbets">${Object.entries(p.bets).map(([t, a]) => `${a.toLocaleString()}`).join(" · ") || ""}</span>
        <span class="pbal">${p.balance.toLocaleString()}</span>
      </div>`,
    )
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

// ---------- boot ----------

wheel = new Wheel(wheelCanvas);
wheel.setTickCallback(() => sfx.tick());
board = new Board(boardEl, {
  place: (target) => { sfx.chip(); api.send("place_bet", { target, amount: selectedChip }); },
  remove: (target) => api.send("remove_bet", { target }),
});

const auto = parseHash();
if (auto) {
  role = auto.spectate ? "spectator" : "player";
  gameId = auto.gameId;
  playerKey = auto.playerKey;
  ($("#join-game") as HTMLInputElement).value = auto.gameId;
  (  $("#join-key") as HTMLInputElement).value = auto.playerKey;
  tryAutoJoin();
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
