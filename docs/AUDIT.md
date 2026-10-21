# PrivateRoulette — Audit (2026-10-20)

Audit performed while FFXIV was in maintenance, against the local source, the
live worker, the latest GitHub release, the combined Dalamud repo and the
deployed Pages site. Updated 2026-10-21 with the `0.8.1.0` chip/timing polish.

## 1. Snapshot

| Item | Value |
|---|---|
| Plugin source version | `0.8.1.0` (`PrivateRoulette.csproj`) |
| Per-plugin repo manifest | `0.8.1.0` (`repo.json`) |
| Latest GitHub release | `v0.8.1.0` — 2026-10-21T00:21:19Z (asset + DLL verified) |
| Latest tag | `v0.8.1.0` (plus `backup-pre-revamp`) |
| Backend health | `{"ok":true,"service":"private-roulette-api","version":"v12"}` |
| Combined repo | PrivateRoulette `0.8.1.0` (GitHub API; raw CDN may lag ~5 min) |
| Pages JS asset | `assets/index-fmBWe-L5.js` (current build) |
| Working tree | clean |
| Web demo | live (`#/demo`, playable, shared payout table) |

## 2. Component inventory

### Plugin (C# / Dalamud)
- `Plugin.cs` — DI, whitelist gate, main-thread queue, chat wiring
  (`RunCommand`/`SendChatText` → direct chat box, not `ProcessCommand`).
- `Services/SocketService.cs` — WebSocket client, hello/auth, event dispatch,
  reconnect, heartbeat.
- `Services/RouletteSession.cs` — session state, game/player/bet actions,
  spin flow, party reveal (23.9s delay), dealer web URL, VIP toggles.
- `Services/DiceService.cs` — `/random` chat parser (EN/DE/FR/JP), character
  context; announcements queue (currently unwired).
- `Services/TradeService.cs` — trade addon open/close, gil delta, partner
  matching fallbacks.
- `Services/ChatSender.cs` — `Utf8String.FromString` →
  `UIModule::ProcessChatBoxEntry` → `Dtor(true)`.
- `UI/MainWindow.cs` — dealer console (games, players, adjustments, VIP,
  cashout, link/tell, spin controls, ledger/CSV, stats, settings).
- `UI/WheelWindow.cs` — in-game wheel (ImGui draw list, 23s rotor-relative).
- `UI/PluginUi.cs` — window system and UI hooks.

### Backend (Cloudflare Worker + Durable Object)
- `worker.ts` — `/health`, `/ws` upgrade, single DO id `global-${DO_VERSION}`.
- `table.ts` — auth, games, players, VIP, balances, bets, spin modes,
  provable-fairness result, payouts, winner results, ledger, stats, chat,
  cashout, end/delete, public/snapshot views, hibernation-safe broadcasts.
- `fairness.ts` — sha256 formula (`% 38`), wheel order, colors, payout table,
  target validation, bet labels.

### Web (static, GitHub Pages)
- `main.ts` — hash routes, player/spectator/dealer/demo, WebSocket, optimistic
  betting, deferred reveal, chat buffering, dealer console, demo game engine.
- `board.ts` — full American board, per-cell edge zones, per-player chips,
  color classes.
- `wheel.ts` — canvas wheel face (wood/gold/pockets/frets), top-down ball
  animation (independent rotor/ball progress, fret bumps).
- `style.css` — felt + wood frame, glossy cells, uniform outside row, casino
  chips, spotlights, layout.

## 3. Feature matrix

| Area | Feature | Status |
|---|---|---|
| Access | Plugin whitelist (`AuthorizedDealers`, default Akari Zanto@Adamantoise) | ✅ |
| Access | Dealer token auth (worker secret), no socket for unauthorized chars | ✅ |
| Access | Player links with per-player keys; spectators never see keys | ✅ |
| Games | create/join/list/end/delete, spin modes control/random/manual | ✅ |
| Players | add by name/target, buy-in, VIP, free play, away/ready | ✅ |
| Players | VIP toggle after seating (plugin + web) | ✅ |
| Money | trade auto-credit (gil delta at trade close) | ✅ verified live |
| Money | manual adjustment +/-, record payout/tips (as adjustments) | ✅ |
| Limits | per-spot inside 500k / outside 250k, total per player 500k; VIP 1M/500k/1M | ✅ verified |
| Bets | straight, split, street, corner, six-line, firstfour, dozens, columns, even-money; `topline` is server-only (no TOP cell) | ✅ |
| Spin | `/random` capture, manual dice/number, provable fairness, proof display | ✅ |
| Results | winner breakdown (player, bet, net, label) in `round_result` | ✅ |
| Reveal | web header/list/chat frozen until wheel lands; in-game party delay | ✅ verified |
| Chat | web table chat, system winner lines, in-game party announce | ✅ |
| Dealer | web console, plugin console, ledger + CSV, stats | ✅ |
| Web | player/spectator/dealer routes, 00 board, colored chips, uniform row | ✅ |
| Web | playable demo (bet, NPCs, payouts, countdown, rebet/clear/cashout) | ✅ |
| Web | responsive resize (wheel ResizeObserver, hidden-view sizing) | ✅ |
| Plugin | in-game wheel + dealer console | ✅ motion now matches web (23s profile/fret bumps) |
| Plugin | table chat panel + dealer chat input | ✅ |
| Plugin | plugin sound effects | removed (web-only audio; dead toggle deleted) |
| Plugin | announcement templates ($Name/$Bets/…) | ✅ auto + manual announcements wired |
| Backend | dealer-only role enforcement on dealer events | ✅ verified against a real player socket |
| Backend | bet flood protection | ✅ 30 bet changes / 2s per connection |
| Web | demo payout table | ✅ imports `server/src/fairness.ts` (no duplicate) |

## 4. Verification evidence (built up during the project)

- **Tells**: outgoing `/tell` verified after switching to
  `UIModule::ProcessChatBoxEntry`; a sig-scanned attempt crashed the game and
  was removed. Cross-DC tells remain impossible (game limitation).
- **Trade auto-credit**: 1,000,000 gil trade credited the player and updated
  the site. Partner matching logs `[trade] …` in `dalamud.log`.
- **Dice**: real line is `Random! You roll a 437.`; parser updated and the
  spin completes. Old regex expected `Random! (1-1000) 437`.
- **Limits**: per-spot rejection (`max bet for this spot is 500000 (you
  already have 50000)`), total cap rejection after the total-cap feature, VIP
  cap test (600k allowed as VIP, then rejected at the normal 500k total after
  UnVIP).
- **Reveal**: early sample during a spin showed the old balance/chat; after
  landing the balance, net and winners line appeared. In-game party delay
  set to 23.9s to match the 23s wheel.
- **Wheel**: pixel sampling at t≈2s → ball on rim (ratio ~0.91); mid-crawl →
  number track (~0.83–0.86); after landing → pocket (~0.69); exact result
  confirmed via balance/chat.
- **Demo**: playable — bets placed/removed with limits, `Spin now`, NPC bets,
  payout winners line including "You", next betting window, rebet, no console
  errors.
- **Dealer page**: `#/dealer/{game}/{token}` auto-logs in, strips the token
  from the URL, opens the game table.
- **Deploy (0.8.0.0)**: CI release succeeded (manifest/assembly both
  `0.8.0.0`, release published 05:48Z); the combined repo API serves
  `0.8.0.0`; the release Pages asset was `index-BCQkavbT.js` (later board
  builds deploy independently; current is `index-BxAO5_8F.js`) and the live
  demo loads; worker `/health` reports `v12`.
- **Deploy (0.8.1.0)**: release manifest/assembly both `0.8.1.0`; Pages
  `index-BxAO5_8F.js` / `index-BTrFdyfx.css`; combined repo API `0.8.1.0`;
  the 0.8.1.0 build used 30px white-outlined chips; demo spin measured
  23.17s. A later web-only chip pass uses 32px dark-medallion chips
  (`index-fmBWe-L5.js` / `index-C_WBEvlM.css`).

## 5. Findings

### P1 — Dealer WebSocket events were not role-gated (security) — **FIXED / verified**
`table.ts` previously dispatched these cases without checking `conn.role`:
`add_player`, `adjust_balance`, `set_player_vip`, `record_payout`,
`request_spin`, `spin`, `clear_bets`, `rebet`, `send_chat`, `get_ledger`,
`get_stats`, `cashout_player`, `end_game`, `delete_game`.
A player connection (`role: "player"` with a valid player_key) could send any
of them. `list_games` and `create_game` were already protected.

**Fix shipped in 0.8.0.0 / backend v12:** a module-level `DEALER_ONLY` set is
checked before the switch, and each bet mutation also passes a per-connection
2s/30-action limiter:

```ts
if (DEALER_ONLY.has(t) && conn.role !== "dealer") {
  return this.err(ws, "dealer only");
}
```

The live worker was attacked with a real player socket after the deploy:
`get_stats`, `add_player` and `adjust_balance` all returned `dealer only`, a
valid bet still worked, and the 31st rapid bet returned
`too many bet changes — slow down`. `DO_VERSION` was bumped to `v12` (fresh
storage, as expected).

### P2 — Documentation drift — **FIXED**
- `README.md` / `server/README.md` fairness text said `% 37`; **corrected to
  `% 38`** (American double-zero).
- The stale "table chat + dealer stats arrive in Phase 2" label and the inert
  "Sound effects (Phase 2)" toggle are **gone**. The plugin now has a live
  table-chat panel with a send box (the web console has the same feed and a
  dealer chat input). Plugin audio has been removed as a promise; the web has
  synthesized sounds.
- The README button labels were updated: **Open spectator page** and
  **Open dealer page**.

### P2 — In-game wheel was behind the web wheel — **FIXED (motion)**
The web wheel received the physics-flavoured revamp (independent rotor/ball
timing, number-track crawl, fret bumps, smoothstepped radius). The plugin
wheel is now ported to the same profile: 23s, 8–10 ball turns, rim → 0.862R
track crawl → 0.69R pocket, 0.8% fret hops, enveloped settle wobble, exact
landing. The post-settle idle-angle jump was also fixed (the old code reset to
a wall-clock angle). The plugin art is still ImGui-drawn, so the web remains
the visual reference. `RouletteSession` party delay stays 23.9s - keep it in
sync if the wheel duration changes.

### P3 — Unwired settings / templates — **FIXED**
`DiceService.Announce` and the `Announcements` templates had no callers;
`AutoAnnounceBets` / `AutoAnnounceResults` were inert; `SoundEffects` only
appeared in the settings UI. **Fixed:** announcements are rendered in
`RouletteSession` and sent to table chat (`GameInfo`/`GameInfoControl` on
create, `LastCall` + `CurrentBets` when the table locks, `Results` on settle),
`Win` is used for the party result line, and the settings expose all toggles.
The dead `DiceService` queue/`Announce` code and the sound setting were
removed.

### P3 — Duplicated demo payout table — **FIXED**
`web/src/main.ts` (`demoPayoutMult`) mirrored `server/src/fairness.ts`.
**Fixed:** the demo now imports `payoutMultiplierForTarget`,
`describeBetTarget` and `isInsideBet` directly from
`../../server/src/fairness` (Vite `server.fs.allow: [".."]` for dev). There is
no duplicate payout table left to drift.

### P3 — Trade partner matching is heuristic
Window text nodes → current target → last target. If a trade is credited to
the wrong player, read the `[trade] opened/unmatched` lines in
`dalamud.log`. The gil delta itself is exact (gil snapshot at open/close).

### P3 — Release/CDN operational traps
- Tags must be four-part and match the manifest: `v0.8.0.0` ↔
  `<Version>0.8.0.0</Version>` ↔ manifest `0.8.0.0`. A 3-part project
  version produces a manifest mismatch; Dalamud rejects it as "Load failed"
  (and the CI guard now fails the build).
- `raw.githubusercontent.com/dexcss/DalamudPlugins` caches `repo.json` for up
  to ~5 minutes (`Cache-Control: max-age=300`). If the installer doesn't see a
  new version immediately, wait or check the GitHub API contents endpoint,
  which bypasses the CDN.
- Durable Object instances stay on the code version they started with;
  backend changes need a `DO_VERSION` bump to apply immediately (and that
  wipes game storage).

## 5a. Remediation verification (0.8.0.0 / backend v12)

| Check | Result |
|---|---|
| Dealer auth | real dealer socket authenticated, created a game, issued a player key |
| Player sends `get_stats` | rejected: `dealer only` |
| Player sends `add_player` | rejected: `dealer only` |
| Player sends `adjust_balance` | rejected: `dealer only` |
| Player places a valid bet | accepted (`bets_updated`) |
| 40 rapid bets | 31st rejected: `too many bet changes — slow down` |
| Spin after the flood | locked, settled on 13, `round_result.results` present |
| Test cleanup | game deleted |
| Plugin build | `dotnet build -c Release` — 0 warnings, 0 errors |
| Web build | TypeScript + Vite — success, demo imports the server payout table |
| Web demo | 1M bankroll, 100k on straight 17 + split 3-6 + corner 3-6-2-5; balance frozen at 1,000,000 during the spin; landed 00; revealed 700,000 and `▼ 300,000`; looped to the next betting window |
| Backend health | `{"ok":true,"service":"private-roulette-api","version":"v12"}` |

**Not tested in-game (maintenance):** the new 0.8.1.0 wheel motion/timing, the plugin
chat send box, and the automatic table-chat announcements. They compile and
use only existing, previously verified APIs, but need a `/pr` smoke test when
the game is back.

## 6. Game limitations (not bugs)

- `/tell` cannot cross data centers. Same-DC only; display-name-only players
  cannot be told.
- Manual spin mode has a `"manual"` proof hash (no dice/control proof).
- `/random` without a range rolls the game's default; the server domain is
  0–999 (1000 maps to 0).

## 7. Rollback / backups

- `backup-pre-revamp` tag = pre-visual-revamp commit (`c5eb0b3`).
- `backups/web-0.6.6-backup.zip` = web source + built assets at that point.
- To revert a release: check out its tag and push; then regenerate the
  combined repo. For the backend, revert code **and** bump `DO_VERSION`.

## 8. Recommended next steps

1. In-game smoke test of 0.8.1.0 (23s wheel motion, chat input, auto
   announcements) once maintenance ends.
2. Optional: batch Durable Object storage writes (the bet flood limiter covers
   the private-table case).
3. Optional: trade-confirmation/reassign UI for ambiguous partners (the
   current fallbacks log the window text and seated names; manual adjustment
   remains available).
4. Optional: dedicated tips button (currently adjustments / `record_payout`).
5. Optional: spectator chat, racetrack/call-bet section, theme support.
