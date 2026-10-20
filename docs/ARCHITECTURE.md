# PrivateRoulette — Architecture

## 1. System overview

```
FFXIV (Dalamud plugin)                 Web browser
 dealer console + in-game wheel        player / spectator / dealer / demo
        │                                   │
        └────────── WebSocket ──────────────┘
                       │
             Cloudflare Worker (/ws)
                       │
             Durable Object "TableRoom"
        one global instance: state, bets, spins,
          ledger, stats, chat, fairness proof
```

- **Dealer** — the Dalamud plugin. Authenticates with a shared dealer token.
  Owns the game, players, trade crediting, spin workflow and in-game wheel.
- **Backend** — a single Cloudflare Worker + one Durable Object instance keyed
  by `global-${DO_VERSION}`. Authoritative for money, bets, spins, ledger.
- **Web** — static Vite/TypeScript app on GitHub Pages. Hash routing means no
  server-side routes. The demo runs a local simulation with no socket.

## 2. Repo map

| Path | Role |
|---|---|
| `Plugin.cs` | entry point, DI, framework tick, whitelist gate, main-thread queue, chat wiring |
| `Configuration/PluginConfig.cs` | persisted config, migrations, chip presets, limits, whitelist |
| `Protocol/ProtocolModels.cs` | JSON models for every wire event |
| `Protocol/Wheel.cs` | American wheel order/colors, `Display("00")`, payout table, target parser, fairness verify |
| `Services/SocketService.cs` | WebSocket connect/hello/reconnect, event fan-out |
| `Services/RouletteSession.cs` | high-level state/actions, spin flow, reveal, dealer web URL |
| `Services/DiceService.cs` | `/random` parsing + character context |
| `Services/TradeService.cs` | trade addon lifecycle + gil delta credit |
| `Services/ChatSender.cs` | in-game chat box send |
| `UI/MainWindow.cs` | dealer console + settings |
| `UI/WheelWindow.cs` | in-game wheel |
| `server/src/worker.ts` | worker fetch, `/health`, DO id/version |
| `server/src/table.ts` | all game logic and storage |
| `server/src/fairness.ts` | sha256 result, wheel order, colors, payouts, labels |
| `web/src/main.ts` | routes, roles, socket client, UI state, demo engine |
| `web/src/board.ts` | board layout, in-between zones, per-player chips |
| `web/src/wheel.ts` | canvas wheel/ball |
| `web/src/style.css` | table/chips/layout |
| `repo.json` | per-plugin Dalamud repo manifest (version + download URL) |
| `.github/workflows/release.yml` | tag build, manifest-vs-tag guard, GitHub release |
| `.github/workflows/pages.yml` | web build + Pages deploy |

## 3. Wire protocol

Envelope both ways: `{ "t": "<event>", "d": { … } }` over `wss://…/ws`.

### Client → server (hello/join)
| Event | Payload | Notes |
|---|---|---|
| `hello` | `{role:"dealer", token, dealer_name, dealer_world}` | token must match `DEALER_TOKEN` |
| `hello` | `{role:"player", game_id, player_key}` | key from invite link |
| `hello` | `{role:"spectator", game_id}` | read-only |
| `list_games` | `{}` | dealer only |
| `create_game` | `{spin_mode, dealer_name, dealer_world, limits, chips}` | dealer only (checked inside) |
| `join_game` | `{game_id}` | all roles |

### Dealer events (role-gated server-side)
`add_player`, `adjust_balance`, `set_player_vip`, `record_payout`,
`request_spin`, `spin`, `clear_bets`, `rebet`, `send_chat`, `get_ledger`,
`get_stats`, `cashout_player`, `end_game`, `delete_game`.
These are checked against the authenticated `conn.role` before dispatch; a
player/spectator socket gets `dealer_error: dealer only`. Bet add/remove/
clear/rebet actions also pass a per-connection flood limiter (30 changes in
any 2s window).

### Player events
`place_bet {target, amount}`, `remove_bet {target}`, `clear_my_bets`,
`rebet_mine`, `set_ready {ready}`, `set_away {away}`, `cashout`,
`send_player_chat {text}`, `ping` → `pong`.

### Server → clients
| Event | Payload (key fields) |
|---|---|
| `hello_ok` | role, player_id/display_name (player), games (dealer) |
| `game_state` | spinning, table_locked, awaiting_dice, spin_mode, control_number, limits (incl. totals), chip_denominations, players |
| `public_bets` | players[] (id, name, display_name, color, bets, ready, away, is_vip, balance, last_round_net) |
| `game_created`, `games_list`, `game_joined`, `game_closed` | game lifecycle |
| `player_created` | player_key (dealer only), display_name, balance |
| `spin` | result, color, proof {dice_roll, control_number, hash, seed, result}, control_number |
| `round_result` | result, color, dice_roll, balances{}, **results[]** (winning bets: player_id, player_name, bet_type, bet_label, bet_amount, payout, net) |
| `table_history` | last 25 results |
| `chat_history` / `chat_message` | id, from_id, from_name, text, role, color, ts |
| `bets_updated` | player's own bets + balance |
| `cashout_ok`, `dealer_ok`, `dealer_error`, `admin_alert` | status |

## 4. Storage model (Durable Object)

One JSON value per game under `game:{gameId}` plus DO memory (`this.state`) as
the live copy. `save()` writes the whole game on mutations.

`GameState` includes: id, dealer name/world, spin mode, control number, bet
limits (inner/outer/total × normal/VIP), chips, status (active/finished/
payouts_complete/deleted), table_locked, spinning, awaiting_dice, round_number,
created_at, players[], chat[], history[], rounds[], all_bets[],
adjustments[], total_tips.

`Player`: id, name (Name@World), display_name, color, player_key, balance,
bets{}, ready, away, is_vip, is_freeplay, last_round_net, joined/left, cashout,
stashed balance, etc.

Ledger records: rounds (result/color/dice/proof/totals), per-bet records
(player, bet_type, amount, payout, timestamp), adjustments (amount, reason).

## 5. Fairness

- 38 pockets: `0`, `00`, `1..36`; **00 is internally 37**, displayed "00".
- `seed = uint32_be(sha256("{dice_roll}:{control_number}")[0..8])`
- `result = seed % 38` (American double-zero).
- `hash` = first 16 hex chars of sha256, `seed` included in the proof.
- Dice domain 0–999 (`/random 1000` value 1000 maps to 0).
- Manual mode → result forced, `proof.hash = "manual"`, seed 0.
- Random mode → server rolls a crypto dice (still auditable).
- Control number is per game (8 hex chars), shown in the UI and used in the
  proof.

## 6. Bet keys & payouts

Wheel order (clockwise): `0,28,9,26,30,11,7,20,32,17,5,22,34,15,3,24,36,13,1,
37,27,10,25,29,12,8,19,31,18,6,21,33,16,4,23,35,14,2`.

Target keys:
- `n-0` … `n-37` (37 = 00)
- `split-A-B`, `street-A-B-C`, `corner-A-B-C-D`, `sixline-…` (numbers unordered on the wire; validation checks count/range)
- `topline` (0-00-1-2-3), `firstfour` (0-00-1-2-3 basket)
- `dozen-1..3`, `col-1..3` (col-1 = 1,4,7…; col-3 = 3,6,9…)
- `red`, `black`, `odd`, `even`, `low` (1–18), `high` (19–36)

Payout multipliers (total returned, including stake):
| Bet | Multiplier | Odds |
|---|---|---|
| Straight | 36 | 35:1 |
| Split | 18 | 17:1 |
| Street | 12 | 11:1 |
| Corner | 9 | 8:1 |
| Six-line | 6 | 5:1 |
| Top line / first four | 7 / 9 | 6:1 / 8:1 |
| Dozen / column | 3 | 2:1 |
| Red/black/odd/even/low/high | 2 | 1:1 |

Outside bets lose on either zero. `isInsideBet` returns true for n-/split/
street/corner/sixline/topline/firstfour; limits use that split.

## 7. Spin lifecycle

1. Dealer presses **Request spin** → `request_spin` → server locks table,
   `awaiting_dice=true` in control mode, broadcasts `game_state`.
2. Dealer rolls `/random` (plugin parses the chat line) or enters a number →
   `spin {dice_roll}` (or `{number}` in manual).
3. Server computes result/proof, settles all bets, writes the ledger, clears
   bets, unlocks, then broadcasts in order: `spin`, `round_result` (with
   `results`), `table_history`, `public_bets`, `game_state`, then a system
   chat line "Number N (color)! Name +X on bet; …".
4. Clients animate for 28s. The web freezes balances/chat until the wheel
   lands (`pendingRound` + wheel settle) then reveals; the plugin delays the
   party announcement 28.9s.
5. Next round: players bet again; dealer requests another spin.

**Announcements.** The plugin renders the configurable templates into table
chat: `GameInfo`/`GameInfoControl` on game creation, `LastCall` +
`CurrentBets` when the table locks (if `AutoAnnounceBets`), `Results` on
`round_result` (if `AutoAnnounceResults`), and `Win` for the delayed party
result line. The dealer can also send a manual table-chat line from the plugin
panel; the web dealer console has the same input. The web buffers chat during
the spin and flushes it in `revealRound()`.

## 8. Web client

- **Routes** (hash, because GitHub Pages):
  - `#/{gameId}/{playerKey}` — player
  - `#/spectate/{gameId}` — spectator
  - `#/dealer/{gameId}/{token}` — dealer console, auto-login; token stripped
    from the address bar after load (`history.replaceState`)
  - `#/demo` — fully playable local demo (no socket)
- **Roles**: player (bet UI), spectator (read-only board + aggregate chips),
  dealer (dealer bar + game list), demo (local player + NPCs).
- **Board**: American layout; zeros stacked left; numbers 3..36 top row;
  uniform six-box even-money row; TOP under the zeros. In-between targets are
  **visible gold lines/dots** drawn by `.cellzone::before` inside 24px per-cell
  overlays centered on the middle of the 8px grid gap (a 4px offset from the
  cell edge). The top row mirrors the street/six-line hit targets (chips stay
  on the bottom line so a bet is not drawn twice). The zero column adds the
  0/00 split, 0/1 and 00/3 splits, the 0-1-2 / 00-2-3 trios and first four
  (0/1/2/3). Zones are above cells (`z-index: 8`); avoid `filter` on hover
  (stacking context clips chips). The spin blocker is `z-index: 60` and renders
  a gold-bordered box above every cell, line, dot and chip.
- **Chips**: one `.pchip` per player per target, colored by the server's
  player color, stable order (own chips first, then player id), DOM reuse
  keyed by player id.
- **Optimistic betting**: place/remove push a pending op and render
  immediately; server `public_bets`/`bets_updated` reconcile (`expected`
  amount per target); errors clear pending. One render per animation frame.
- **Reveal**: `renderBalance`/`renderPlayers`/chat are frozen while spinning
  or `pendingRound`; `revealRound()` (wheel settle) forces the balance, flash,
  player list and buffered chat, then starts the next demo round if in demo.
- **Wheel**: pre-rendered face (wood/gold/pockets/frets/groove, fixed gloss
  overlay) rotated each frame; ball animated top-down with independent rotor
  and relative-ball progress, a long crawl on the number track, tiny slow
  fret bumps, smoothstepped radius transitions and an exact landing. 28s.
- **Demo**: local engine **imports the backend payout table** from
  `server/src/fairness.ts` (Vite `server.fs.allow: [".."]`), 20s betting window,
  countdown ticker, NPC random bets, `Spin now`, payout/results chat,
  rebet/clear/cash-out, loops forever.

## 9. Config, auth, and limits

- `PluginConfig`: `ApiUrl`, `DealerToken`, `AuthorizedDealers` (default
  `Akari Zanto@Adamantoise`), spin mode, per-spot/total limits + VIP, chip
  presets, announcement templates/toggles, party announce, auto-open wheel,
  verbose wire log, trade throttle. Plugin sound effects are intentionally not
  part of the config (the web has audio).
- Whitelist is checked every framework tick from `CharacterContext`
  (name/world/content id). Unauthorized characters never open the socket.
- Backend `DEALER_TOKEN` is a Worker secret. The web dealer token is in
  localStorage; the plugin sends it in `hello`. The Open dealer page button
  passes it via the URL hash (never sent to a server) and the page removes it.
- Limits are enforced server-side per spot and per player total; VIP variants
  apply. The demo mirrors these limits locally.

## 10. Deployment topology

- Plugin: GitHub tag `vX.Y.Z.0` → `release.yml` builds/guards/publishes
  `PrivateRoulette.zip`.
- Combined repo `dexcss/DalamudPlugins` regenerated from per-plugin
  `repo.json`s via `Build-CombinedRepo.ps1`.
- Backend: `cd server && npx wrangler deploy`; secrets via
  `npx wrangler secret put DEALER_TOKEN`; `/health` reports `DO_VERSION`.
- Web: any push to `main` touching `web/**` → `pages.yml` → GitHub Pages.
