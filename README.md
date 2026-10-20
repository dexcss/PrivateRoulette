# Private Roulette

Fully self-hosted dealer roulette for FFXIV. No third-party services — the
plugin, the backend and the web client are all yours.

```
┌──────────────────────┐   WebSocket   ┌──────────────────────────┐   WebSocket   ┌─────────────────────────┐
│ PrivateRoulette      │◄─────────────►│ Your Cloudflare Worker    │◄─────────────►│ Your web client          │
│ (Dalamud, in-game)   │   JSON        │ + Durable Object          │   JSON        │ (GitHub Pages)           │
│ dealer console+wheel │               │ games, bets, ledger,      │               │ betting, spin, chat      │
└──────────────────────┘               │ provable-fairness spins   │               └─────────────────────────┘
                                       └──────────────────────────┘
```

## For the dealer (quick start)

1. Update the plugin from your Dalamud plugin repo, `/pr`.
2. First run: paste your dealer token (matching the worker's `DEALER_TOKEN` secret).
3. **Create control game** → the dealer console opens.
4. **Add player** → enter `Name@World`, buy-in, optionally VIP / free play.
   **Copy link** and send it to the player.
5. They open the link on any device — bets go on the board, chips stack live.
6. When everyone has bet: **Request spin** → roll `/random` in game (the plugin
   captures the result automatically, or type the number) → the in-game wheel
   and the website wheel spin together.
7. Result + provable-fairness proof appear everywhere; balances and the ledger
   update automatically.

Extra tools: **Open dealer page** (auto-login dealer web console),
**Open spectator page**, **Show wheel**, dealer stats, per-round ledger with
CSV export, table chat, tips/payouts via balance adjustments, per-player
cash-out, VIP/UnVIP toggles. A fully playable demo lives at
`https://dexcss.github.io/PrivateRoulette/#/demo` (no backend needed).

## Features

- **American double-zero wheel:** 38 pockets (0, 00, 1–36), both zeros green,
  `00` encoded as 37 internally. Provable fairness:
  `result = uint32(sha256("{dice}:{control}")[0..8]) % 38`, verified
  client-side on the site and in the plugin. Control number is issued per game
  by your backend.
- **All bet types:** straight, split, street, corner, six-line, top line
  (0-00-1-2-3), first-four, dozens, columns, red/black, odd/even, low/high —
  with the standard payouts.
- **Live experience:** animated wheel with ball physics on both ends (procedural
  art, no borrowed assets), synthesized sound effects, hot/cold stats, result
  history, chat, per-player colored chips.
- **Dealer control:** spin modes (control / random / manual), per-spot and
  per-player total bet limits with VIP variants, chips, ready states, rebet,
  clear, cash-out, tips, adjustments, VIP toggle after seating.
- **Ledger:** every round, bet, payout and adjustment persisted server-side;
  CSV export from the plugin.
- **Access control:** plugin whitelist + dealer token; players only join via
  per-player links; spectators never see player keys.
- **Deferred reveal:** balance/result/chat stay hidden until the wheel lands
  on both the website and in-game party chat.

## Repo layout

- `private roulette plugin source` (repo root) — Dalamud plugin, C# / net10
- `server/` — Cloudflare Worker + Durable Object (TypeScript)
- `web/` — player/spectator web client (Vite + TypeScript, no framework)
- `.github/workflows/` — `release.yml` (plugin zip) and `pages.yml` (web deploy)
- `docs/` — audit, architecture, runbook, testing playbook
- `HANDOFF.md` — current state, deployments, known issues, next steps

## Documentation

- `HANDOFF.md` — current state, live versions, known issues, next steps
- `docs/ARCHITECTURE.md` — components, protocol, storage, fairness, bet table
- `docs/RUNBOOK.md` — dealer ops, plugin/backend/web deploys, rollback, troubleshooting
- `docs/TESTING.md` — demo mode, test scripts, verification recipes
- `docs/AUDIT.md` — 2026-10-20 audit with findings (incl. backend role-gating P1)

## Deploying

- **Plugin:** push a `v*` tag → CI builds `PrivateRoulette.zip`.
- **Web:** push to `main` touching `web/**` → Pages redeploys automatically.
- **Backend:** `cd server && npx wrangler deploy` (see `server/README.md`).
  Note: Durable Object instances keep the code version they started with —
  bump `DO_VERSION` in `server/src/worker.ts` when a backend change must take
  effect immediately (this starts a fresh instance and resets game storage).
