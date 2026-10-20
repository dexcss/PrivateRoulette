# PrivateRoulette — Handoff

> Fully self-hosted dealer roulette for FFXIV: Dalamud plugin (in-game dealer
> console + wheel), Cloudflare Worker + Durable Object backend, and a static
> web client (players/spectators/dealer console/playable demo).
>
> **Current release: `0.8.0.0`** (tag `v0.8.0.0`) · backend **DO v12** ·
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
| Plugin release | `github.com/dexcss/PrivateRoulette/releases` | `v0.8.0.0` |
| Combined Dalamud repo | `github.com/dexcss/DalamudPlugins` (`repo.json`) | PrivateRoulette `0.8.0.0` |
| Backend | `wss://private-roulette-api.dexcss-roulette.workers.dev/ws` | DO `v12` |
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
with long number-track crawl and fret bumps); the in-game wheel now uses the
same timing/radius profile. A tilted 3D wheel experiment was tried and reverted
at the user's request — the top-down view is the intended look.

**Post-audit hardening (0.8.0.0):** dealer-only WebSocket events are now
role-gated server-side, bet mutations have a per-connection flood limit, the
plugin dealer console has a live table-chat input, the announcement templates
are wired (`GameInfo`, `LastCall`, `CurrentBets`, `Results`, `Win`), the inert
sound toggle was removed, and the web demo imports the backend's payout table
instead of duplicating it. The backend is on `v12`; the auth fix was verified
by attacking it with a real player socket (all dealer events rejected).

**Web board update (post-0.8.0.0):** every split/street/corner/six-line now
has a visible thin champagne line or small dot hit target (subtle at rest,
bright gold on hover); the top-row mirrors and the TOP cell were removed (the
backend still accepts `topline`, but the board does not expose it); the zero
column supports 0/00, 0/1 and 00/3 splits, 0-1-2 / 00-2-3 / 0-00-2 trios and
first four; hovering any bet glows every number it covers (Red → all 18 red cells,
dozens/columns → 12, splits/streets/corners/six-lines → their numbers); and
the spin blocker is a `z-index: 60` gold-bordered box that covers every cell,
line, dot and chip. Web-only deploy — the plugin/backend versions are
unchanged.

## Known issues / next steps

1. Trade partner matching is still heuristic (trade-window text → target →
   last target). If it misses, use the manual balance adjustment; the
   `[trade] unmatched …` log line includes the window text and seated names.
2. `save()` writes the whole game on every bet. The new rate limit (30 bet
   changes / 2s per connection) keeps a private table safe; a very large
   automated table would need batched storage writes.
3. The in-game wheel now matches the web's **motion**, but its art is still
   ImGui-drawn (flat pockets, no canvas gradients). The web remains the
   premium visual reference.
4. The party reveal delay is hard-coded to 28.9s in `RouletteSession` to match
   the 28s wheel. If either duration changes, update both.
5. Plugin sound effects are not implemented (the web has synthesized audio);
   the dead setting was removed. Tips are recorded via adjustments /
   `record_payout`; there is no dedicated tip button yet.
6. `/tell` is same-data-center only (game limitation).

## Rollback

- Visual revamp backup: tag **`backup-pre-revamp`** (`c5eb0b3`) and
  `backups/web-0.6.6-backup.zip` (source + built assets at that point).
- Any release: check out its tag and push, or re-point the combined repo.
- Backend: reverting code requires a `DO_VERSION` bump to take effect on the
  live Durable Object (which resets game storage).
