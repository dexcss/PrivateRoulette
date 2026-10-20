# PrivateRoulette — Handoff

> Fully self-hosted dealer roulette for FFXIV: Dalamud plugin (in-game dealer
> console + wheel), Cloudflare Worker + Durable Object backend, and a static
> web client (players/spectators/dealer console/playable demo).
>
> **Current release: `0.7.0.0`** (tag `v0.7.0.0`) · backend **DO v11** ·
> web demo at `https://dexcss.github.io/PrivateRoulette/#/demo`

This is the first file to read in a new session. Then:

| Doc | What it covers |
|---|---|
| `docs/AUDIT.md` | Full audit: features, verification evidence, findings, risks, next steps |
| `docs/ARCHITECTURE.md` | Components, data flow, protocol, storage, fairness, payout/bet tables |
| `docs/RUNBOOK.md` | Dealer ops, build/release, backend/web deploys, rollback, troubleshooting |
| `docs/TESTING.md` | Test scripts, demo, browser and deployment verification recipes |
| `../ffxiv-plugin-notes/15-private-roulette-state.md` | Condensed state note in the shared FFXIV notes collection |
| `../ffxiv-plugin-notes/16-chat-tells-and-dice.md` | Reusable: chat send, `/tell`, `/random` parsing, trade hooks |
| `../ffxiv-plugin-notes/17-cloudflare-durable-objects.md` | Reusable: Workers/DO, hibernation, DO version pinning, auth |
| `../ffxiv-plugin-notes/18-web-roulette-ui.md` | Reusable: roulette board/wheel web techniques and pitfalls |

## Repo layout

```
PrivateRoulette/            Dalamud plugin (C# / net10.0-windows, Dalamud.NET.Sdk 15)
  Plugin.cs                 entry point, DI, main-thread queue, chat wiring
  Configuration/            plugin config + migrations
  Protocol/                 wire models, fairness verifier, wheel order/payouts
  Services/                 socket, session, dice, trade, chat sender
  UI/                       dealer console, wheel window, settings
server/                     Cloudflare Worker + Durable Object (TypeScript)
  src/worker.ts             routing, /health, DO_VERSION
  src/table.ts              all game state, bets, spins, ledger, chat
  src/fairness.ts           sha256 fairness, colors, payout table, target validation
web/                        Vite + TypeScript static client (GitHub Pages)
  src/main.ts               routing, dealer/player/spectator/demo, state, betting, reveal
  src/board.ts              American board, zones, per-player chips
  src/wheel.ts              canvas wheel + ball animation (top-down)
  src/style.css             felt/wood table, chips, cells, layout
backups/                    web snapshots (rollback artifacts)
.github/workflows/          release.yml (plugin zip), pages.yml (web deploy)
repo.json                   per-plugin Dalamud repo manifest
```

## Live deployments

| Thing | Where | Current |
|---|---|---|
| Plugin release | `github.com/dexcss/PrivateRoulette/releases` | `v0.7.0.0` |
| Combined Dalamud repo | `github.com/dexcss/DalamudPlugins` (`repo.json`) | PrivateRoulette `0.7.0.0` |
| Backend | `wss://private-roulette-api.dexcss-roulette.workers.dev/ws` | DO `v11` |
| Web client | `https://dexcss.github.io/PrivateRoulette/` | Pages from `main` |
| Web demo | `…#/demo` | playable, no backend |

## Quick dealer flow

1. `/pr` → paste the dealer token (matches the worker `DEALER_TOKEN` secret) → create a game.
2. Add players (`Name@World`, buy-in, VIP/free play) → trade gil (auto-credits the balance) or adjust manually.
3. Send the per-player invite link (`Link`/`Tell`) — `https://dexcss.github.io/PrivateRoulette/#/{gameId}/{playerKey}`.
4. Players bet; **Request spin** (control mode) → roll `/random 1000` or type the dice → the wheel spins on both ends for 28s.
5. At landing: balance reveal + winners line + party chat (`Number N (color)! Name +X on <bet>; …`).
6. Cash out players (trade back). Ledger/CSV in the dealer console/web console.

`Tell` is limited to the same data center by the game. The `Open dealer page`
button copies nothing — it **opens** the dealer web console with the token in
the URL fragment; the page strips the token from the address bar after loading.

## Current state (one paragraph)

All core features are implemented and were verified live during the build:
trade auto-credit, tells, player/spectator web clients, dealer web console,
provable-fairness spins, per-player colored chips, VIP toggles, per-spot and
total bet limits, cashout, ledger/CSV, stats, deferred reveal in-game and on
the web, and a fully playable web demo. The web client went through a visual
revamp (felt/wood table, glossy cells, casino chips, physics-flavoured wheel
with long number-track crawl and fret bumps). A tilted 3D wheel experiment was
tried and reverted at the user's request — the top-down view is the intended
look.

## Known issues / next steps

1. **P1 — dealer events are not role-gated on the backend.** A player socket
   can send dealer messages (`add_player`, `adjust_balance`, `set_player_vip`,
   `record_payout`, `request_spin`, `spin`, `clear_bets`, `rebet`,
   `send_chat`, `get_ledger`, `get_stats`, `cashout_player`, `end_game`,
   `delete_game`). Fix: gate those cases with `conn.role === "dealer"`, then
   bump `DO_VERSION` (resets game storage). Details in `docs/AUDIT.md`.
2. README/server README fairness text was `% 37`; **corrected to `% 38`** in
   this docs pass. Stale "Phase 2" labels remain in the plugin; announcement
   templates and `AutoAnnounceBets`/`AutoAnnounceResults`/`SoundEffects` are
   unwired.
3. The **in-game wheel** is still the older ImGui rotor-relative animation;
   the web wheel has the newer physics-flavoured ball. Durations are synced
   at 28s, and the party reveal delay is hard-coded to 28.9s in
   `RouletteSession` — change both together.
4. Plugin UI placeholders: "table chat + dealer stats arrive in Phase 2"
   (`MainWindow.cs`) — the plugin has no table-chat panel (the web console
   does); `SoundEffects` is inert (web-only sounds).
5. `DiceService.Announce` / announcement templates and
   `AutoAnnounceBets`/`AutoAnnounceResults` are not wired to anything yet.
6. The demo duplicates the payout table in `web/src/main.ts`
   (`demoPayoutMult`) — keep it in sync with `server/src/fairness.ts`.
7. Trade partner matching is heuristic (trade window text → target → last
   target); if it misses, check `[trade] unmatched …` in `dalamud.log`.
8. No rate limit on bet placement and `save()` writes the full game per bet —
   fine for a private table, not for hundreds of rapid bettors.

## Rollback

- Visual revamp backup: tag **`backup-pre-revamp`** (`c5eb0b3`) and
  `backups/web-0.6.6-backup.zip` (source + built assets at that point).
- Any release: check out its tag and push, or re-point the combined repo.
- Backend: reverting code requires a `DO_VERSION` bump to take effect on the
  live Durable Object (which resets game storage).
