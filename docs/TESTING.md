# PrivateRoulette — Testing playbook

## 0. Fastest checks

| What | How |
|---|---|
| Web look/feel, wheel, board, betting | Open `https://dexcss.github.io/PrivateRoulette/#/demo` (no backend). |
| Plugin builds | `dotnet build PrivateRoulette.csproj -c Release` |
| Backend typechecks | `cd server && npm run typecheck` |
| Web typechecks/builds | `cd web && npx tsc --noEmit && npx vite build` |
| Worker health | `curl https://private-roulette-api.dexcss-roulette.workers.dev/health` |

## 1. Playable demo (no FFXIV, no backend)

`#/demo` is a complete local game:

- 20s betting window / **Spin now**; 28s wheel; payouts; winners chat;
  balances; NPCs re-bet each round.
- Test: place a 100k chip on a number, a split, a corner; header shows
  `1,000,000 gil · X left`; right-click removes; place >500k total → limit
  flash; `Spin now` → blocker + "wheel spinning"; after landing → balance
  delta/flash, `Number N (color)! …` chat, next window with new NPC chips;
  Rebet restores the last round; Clear empties; Cash out resets the bankroll.
- The demo imports `payoutMultiplierForTarget` / `describeBetTarget` /
  `isInsideBet` directly from `server/src/fairness.ts`, so a payout change is
  automatically reflected in both. If the import breaks, check the Vite
  `server.fs.allow: [".."]` setting.

## 2. Backend end-to-end (Node WebSocket scripts)

The `%TEMP%\opencode\pr_*.mjs` scripts (from the build sessions) exercise the
live worker with the dealer token:

| Script | Purpose |
|---|---|
| `pr_authcheck.mjs` | **0.8.0.0 security smoke:** dealer creates a game; a real player socket tries `get_stats`/`add_player`/`adjust_balance` (must be rejected), places a valid bet, floods 40 bets (31st must be rate-limited), dealer spins, results present, game deleted |
| `pr_e2e.mjs` | auth → create game → add player → place bets → spin → payout → stats → cashout |
| `pr_limitcheck.mjs` | per-spot/total cap rejection, `round_result.results` payload |
| `pr_vipcheck.mjs` | VIP toggle, 600k allowed as VIP, normal total rejected after UnVIP |
| `pr_reveal_setup.mjs` / `pr_reveal_spin.mjs` | manual game + player + bets, then lock/spin |
| `pr_state.mjs <game>` | join as dealer and dump limits, players and bets |
| `pr_delgame.mjs <game>` | delete one test game |
| `pr_newgame.mjs` / `pr_addplayer.mjs` / `pr_spin.mjs` / `pr_cleanup.mjs` | older helpers |

Typical manual check:

```
node %TEMP%\opencode\pr_limitcheck.mjs
# expects: "max total bet is 500000 (you already have 300000)",
# then a winning-bet results array from round_result
```

All scripts use `%TEMP%\opencode\pr_dealer_token.txt` (must match the Worker
secret). **Clean up test games** afterwards with `pr_delgame.mjs` or
`pr_cleanup.mjs` (the latter deletes ALL games — only use it when there are
no real tables!).

## 3. Browser verification recipes

### Deferred reveal
1. Create a manual game with a player, place bets (or open the demo and spin).
2. During the spin sample the DOM: `#balance`, `.player .pbal`, `#chat-log`
   must not show the new result yet; `#betting-blocker` visible for players.
3. After landing: balance/net/chat update; winners line includes bet labels.

### Wheel ball
- Use `canvas.getContext('2d').getImageData` to find the brightest (ivory)
  pixel and report its radius ratio. Expected: ~0.91 (rim) early, ~0.83–0.86
  (number track) mid-crawl, ~0.69 (pocket) after settle. The spread across
  frames during the crawl should be small (grid-limited), not the old 5%
  teleport kicks.
- Check no console errors (only a harmless `getImageData willReadFrequently`
  warning from the sampling itself).

### Board geometry
```js
const z = document.querySelector('.cellzone[data-target="split-3-6"]');
const a = document.querySelector('.cell[data-target="n-3"]').getBoundingClientRect();
const b = document.querySelector('.cell[data-target="n-6"]').getBoundingClientRect();
// zone center should be (a.right + b.left) / 2 (within ~1px)
```
- Uniform outside row: the six even-money cells must have equal widths;
  dozens equal; number cells equal.
- Corner chip: centered on the four-cell intersection, fully visible.

### Dealer web route
Open `#/dealer/{game}/{token}` → token stored, hash rewritten to
`#/dealer/{game}`, dealer bar + game table visible. With an empty token → the
dealer card appears.

### Resize/hidden-view sizing
Open the table from the landing (hidden at boot) and confirm the canvas is not
stuck at 280px; the ResizeObserver + `refreshSize()` handle it.

## 4. Release verification

After tagging and the workflows finish:

1. Download `releases/download/vX.Y.Z.0/PrivateRoulette.zip`.
2. Read `PrivateRoulette.json` → `AssemblyVersion` = `X.Y.Z.0`.
3. Read `PrivateRoulette.dll` assembly name → `X.Y.Z.0`.
4. `raw.githubusercontent.com/dexcss/DalamudPlugins/main/repo.json` →
   PrivateRoulette `X.Y.Z.0` (CDN may lag up to 5 min; the GitHub API contents
   endpoint is authoritative).
5. Pages: check the deployed `index.html` asset hashes changed and the demo
   link/feature is present.

## 5. Regression checklist before a public table

- [ ] Plugin loads (correct version, no "Load failed").
- [ ] Whitelist authorizes the dealer; no socket for others.
- [ ] Create a game with the intended limits/VIP caps/chips.
- [ ] Add player, trade gil, balance auto-credits.
- [ ] Invite link and Tell (same-DC) work.
- [ ] Player bets all types (straight/split/street/corner/six-line/topline/
      dozens/columns/even-money); limits reject correctly.
- [ ] Spectator sees the board and aggregate chips; cannot bet.
- [ ] Request spin + `/random` capture; wheel 28s on web and in-game.
- [ ] In-game wheel lands exactly, then idles without a post-settle jump;
      ball crawls the number track with tiny fret hops.
- [ ] Plugin table-chat input posts to the web and the dealer web console chat
      input posts back.
- [ ] Auto announcements: game info on create, last call/current bets on lock,
      results after settle; party `Win` line after 28.9s.
- [ ] Reveal deferred on web; party announcement after the wheel.
- [ ] Winners line lists player/bet/profit; balances/net correct.
- [ ] VIP toggle changes caps; cashout works; ledger/CSV export.
- [ ] Demo link still playable after the web deploy.
