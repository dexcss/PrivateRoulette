# PrivateRoulette — Audit (2026-10-20)

Audit performed while FFXIV was in maintenance, against the local source, the
live worker, the latest GitHub release, the combined Dalamud repo and the
deployed Pages site.

## 1. Snapshot

| Item | Value |
|---|---|
| Plugin source version | `0.7.0.0` (`PrivateRoulette.csproj`) |
| Per-plugin repo manifest | `0.7.0.0` (`repo.json`) |
| Latest GitHub release | `v0.7.0.0` — 2026-10-20T04:18:50Z |
| Latest tag | `v0.7.0.0` (plus `backup-pre-revamp`) |
| Backend health | `{"ok":true,"service":"private-roulette-api","version":"v11"}` |
| Combined repo | PrivateRoulette `0.7.0.0` (raw confirmed) |
| Pages JS asset | `assets/index-BVZXvtUv.js` |
| Working tree | clean |
| Web demo | live (`#/demo`, playable) |

## 2. Component inventory

### Plugin (C# / Dalamud)
- `Plugin.cs` — DI, whitelist gate, main-thread queue, chat wiring
  (`RunCommand`/`SendChatText` → direct chat box, not `ProcessCommand`).
- `Services/SocketService.cs` — WebSocket client, hello/auth, event dispatch,
  reconnect, heartbeat.
- `Services/RouletteSession.cs` — session state, game/player/bet actions,
  spin flow, party reveal (28.9s delay), dealer web URL, VIP toggles.
- `Services/DiceService.cs` — `/random` chat parser (EN/DE/FR/JP), character
  context; announcements queue (currently unwired).
- `Services/TradeService.cs` — trade addon open/close, gil delta, partner
  matching fallbacks.
- `Services/ChatSender.cs` — `Utf8String.FromString` →
  `UIModule::ProcessChatBoxEntry` → `Dtor(true)`.
- `UI/MainWindow.cs` — dealer console (games, players, adjustments, VIP,
  cashout, link/tell, spin controls, ledger/CSV, stats, settings).
- `UI/WheelWindow.cs` — in-game wheel (ImGui draw list, 28s rotor-relative).
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
| Bets | straight, split, street, corner, six-line, topline, firstfour, dozens, columns, even-money | ✅ |
| Spin | `/random` capture, manual dice/number, provable fairness, proof display | ✅ |
| Results | winner breakdown (player, bet, net, label) in `round_result` | ✅ |
| Reveal | web header/list/chat frozen until wheel lands; in-game party delay | ✅ verified |
| Chat | web table chat, system winner lines, in-game party announce | ✅ |
| Dealer | web console, plugin console, ledger + CSV, stats | ✅ |
| Web | player/spectator/dealer routes, 00 board, colored chips, uniform row | ✅ |
| Web | playable demo (bet, NPCs, payouts, countdown, rebet/clear/cashout) | ✅ |
| Web | responsive resize (wheel ResizeObserver, hidden-view sizing) | ✅ |
| Plugin | in-game wheel + dealer console | ✅ |
| Plugin | table chat panel | ❌ placeholder (web has it) |
| Plugin | plugin sound effects | ❌ config inert |
| Plugin | announcement templates ($Name/$Bets/…) | ⚠️ code exists, not wired |
| Backend | dealer-only role enforcement on dealer events | ❌ **P1 finding** |

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
  set to 28.9s to match the 28s wheel.
- **Wheel**: pixel sampling at t≈2s → ball on rim (ratio ~0.91); mid-crawl →
  number track (~0.83–0.86); after landing → pocket (~0.69); exact result
  confirmed via balance/chat.
- **Demo**: playable — bets placed/removed with limits, `Spin now`, NPC bets,
  payout winners line including "You", next betting window, rebet, no console
  errors.
- **Dealer page**: `#/dealer/{game}/{token}` auto-logs in, strips the token
  from the URL, opens the game table.
- **Deploy**: release zip manifest/assembly match the tag; combined repo
  version matches; Pages assets updated; worker `/health` version matches.

## 5. Findings

### P1 — Dealer WebSocket events are not role-gated (security)
`table.ts` dispatches these cases without checking `conn.role`:
`add_player`, `adjust_balance`, `set_player_vip`, `record_payout`,
`request_spin`, `spin`, `clear_bets`, `rebet`, `send_chat`, `get_ledger`,
`get_stats`, `cashout_player`, `end_game`, `delete_game`.
A player connection (`role: "player"` with a valid player_key) can send any of
them. `list_games` and `create_game` are protected (one checks inline, one
inside `createGame`).

**Fix** (before the next public session): add a dealer-only set before the
switch and reject others, e.g.:

```ts
const DEALER_ONLY = new Set([
  "add_player","adjust_balance","set_player_vip","record_payout","request_spin",
  "spin","clear_bets","rebet","send_chat","get_ledger","get_stats",
  "cashout_player","end_game","delete_game",
]);
if (DEALER_ONLY.has(t) && conn.role !== "dealer") {
  return this.err(ws, "dealer only");
}
```

Then bump `DO_VERSION` to force the live DO onto the new code (resets game
storage). Also consider rate-limiting bets and avoiding `save()` per bet.

### P2 — Documentation drift (partially fixed in this pass)
- `README.md` / `server/README.md` fairness text said `% 37`; **corrected to
  `% 38`** (American double-zero) in this docs pass.
- `MainWindow.cs` still shows `"table chat + dealer stats arrive in Phase 2"`
  and `"Sound effects (Phase 2)"`. Table chat exists on the web; the plugin
  panel does not. Sound is web-only.
- The README button labels were updated: **Open spectator page** and
  **Open dealer page**.

### P2 — In-game wheel is behind the web wheel
The web wheel received the physics-flavoured revamp (independent rotor/ball
timing, number-track crawl, fret bumps, smoothstepped radius). The plugin
wheel is the older ImGui rotor-relative animation. Durations are synced (28s)
and `RouletteSession` party delay is 28.9s — if either changes, keep them in
sync.

### P3 — Unwired settings / templates
`DiceService.Announce` and the `Announcements` templates have no callers;
`AutoAnnounceBets` / `AutoAnnounceResults` are inert; `SoundEffects` only
appears in the settings UI. Decide: wire them or remove them.

### P3 — Duplicated demo payout table
`web/src/main.ts` (`demoPayoutMult`) mirrors `server/src/fairness.ts`. Any
payout change must be applied in both places.

### P3 — Trade partner matching is heuristic
Window text nodes → current target → last target. If a trade is credited to
the wrong player, read the `[trade] opened/unmatched` lines in
`dalamud.log`. The gil delta itself is exact (gil snapshot at open/close).

### P3 — Release/CDN operational traps
- Tags must be four-part and match the manifest: `v0.7.0.0` ↔
  `<Version>0.7.0.0</Version>` ↔ manifest `0.7.0.0`. A 3-part project
  version produces a manifest mismatch; Dalamud rejects it as "Load failed"
  (and the CI guard now fails the build).
- `raw.githubusercontent.com/dexcss/DalamudPlugins` caches `repo.json` for up
  to ~5 minutes (`Cache-Control: max-age=300`). If the installer doesn't see a
  new version immediately, wait or check the GitHub API contents endpoint,
  which bypasses the CDN.
- Durable Object instances stay on the code version they started with;
  backend changes need a `DO_VERSION` bump to apply immediately (and that
  wipes game storage).

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

1. Ship the P1 role-gating fix (with DO_VERSION bump, accepting a game reset).
2. Correct the READMEs' fairness text and stale plugin labels.
3. Wire the announcement templates to the web/party chat, or remove the dead
   settings.
4. Bring the plugin wheel up to the web ball quality (or accept the visual
   difference).
5. Add a dedicated tip button (currently tips are adjustments/record_payout).
6. Optional: spectator chat, racetrack/call-bet section, theme support.
