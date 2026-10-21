# PrivateRoulette — Runbook

## 1. Dealer operations (in game)

1. `/pr` — open the dealer console. `/pr config` — settings.
2. Paste the dealer token (matches the Worker `DEALER_TOKEN` secret) once.
3. **Create control game** (or random/manual).
4. **Add player** → `Name@World`, buy-in, VIP/free play. Or target a player and
   **Add targeted**.
5. Trade the player gil: open a trade, put gil in, complete. The plugin
   snapshots dealer gil at open, reads it after close, credits the delta to
   the matched player. (Partner matching: trade-window text → current target →
   last target; if wrong, check `[trade]` lines in `dalamud.log`.)
6. Send the invite: per-player **Link** (clipboard) or **Tell** (whisper with
   "Here is your link to play: …"). Tell only works within the same data
   center; display-name-only seats can't be told.
7. Players bet from their link; watch balances/bets in the console and on the
   web console. Use the **table chat** box under the player list to talk to the
   web table (the web dealer console has the same input). `AutoAnnounceBets`
   posts `LastCall` + a compact current-bets line when the table locks.
8. **Request spin** → roll `/random 1000` in chat (auto-captured) or type the
   dice (0–999). In manual mode, type the number (0–36, 37=00) and Force.
9. The 23s wheel runs; at landing the web and in-game party chat reveal the
   result, winners and balances. `AutoAnnounceResults` posts the formatted
   `Results` template to table chat (buffered by the web until landing); the
   party line uses the `Win` template.
10. **Cash** to cash out a player (trade their balance back), **Adjust** ± for
    tips/payouts, **VIP/UnVIP** to toggle caps. **Open dealer page** opens the
    web dealer console with auto-login; **Open spectator page** opens the
    spectator view; **Show wheel** reopens the in-game wheel window.

### Dealer web console (`#/dealer/{game}/{token}`)
- Auto-login with the token in the hash (stripped after load).
- Game list/create/open/delete, add player, +100k/−100k, cash out, VIP toggle,
  copy invite link, request spin / dice / force, clear/rebet, ledger + CSV,
  and table chat.

### Spectator page (`#/spectate/{game}`)
- Live board with **aggregate** chips per target, player list, history/stats,
  chat (read-only), wheel.

### Playable demo (`#/demo`)
- No backend. You start with 1,000,000; Aria/Bex/Cid bet each round.
- 20s betting window (or **Spin now**), the **same payout table as the
  backend** (imported from `server/src/fairness.ts`), winner chat,
  rebet/clear/cash out (resets the bankroll), loops forever.

## 2. Plugin release runbook

1. Bump the version in **both** places:
   - `PrivateRoulette.csproj` → `<Version>X.Y.Z.0</Version>` (four-part)
   - `repo.json` → `AssemblyVersion` and `TestingAssemblyVersion` `X.Y.Z.0`
2. Build locally: `dotnet build PrivateRoulette.csproj -c Release`.
   Confirm `bin/Release/PrivateRoulette.json` has `"AssemblyVersion": "X.Y.Z.0"`.
3. Commit, tag **`vX.Y.Z.0`** (must match the four-part manifest), push the tag.
4. `release.yml` runs and now **guards** that the packaged manifest matches the
   tag; it publishes `PrivateRoulette.zip`.
5. Regenerate the combined repo:
   - In a clone of `dexcss/DalamudPlugins`, run `Build-CombinedRepo.ps1`
     (it fetches each per-plugin `repo.json` with a cache-buster), commit and
     push.
6. Wait for `raw.githubusercontent.com/dexcss/DalamudPlugins/main/repo.json`
   to update (up to ~5 min; verify via the GitHub API contents endpoint if in a
   hurry). The installer reads that raw URL.
7. Verify the released zip independently:
   - Download `releases/download/vX.Y.Z.0/PrivateRoulette.zip`
   - Read `PrivateRoulette.json` → AssemblyVersion
   - `[Reflection.AssemblyName]::GetAssemblyName("…/PrivateRoulette.dll").Version`

**Common failure:** "Load failed" in Dalamud = manifest version ≠ repo
version. This happened when the csproj was 3-part (`0.5.5`) while the tag/repo
said `0.5.6.0`. Always use the four-part version and the CI guard.

## 3. Backend runbook

```
cd server
npm install
npx wrangler login                 # once
npx wrangler secret put DEALER_TOKEN   # paste the token (matches plugin config)
npx wrangler deploy
curl https://private-roulette-api.dexcss-roulette.workers.dev/health
```

- `src/worker.ts` has `DO_VERSION` (currently `"v12"`). The DO id is
  `global-${DO_VERSION}`. **Durable Objects stay on the code version they
  started with**; bump `DO_VERSION` when a backend change must apply
  immediately. **Bumping resets all game storage** (games/ledger).
- Local dev: `npm run dev` → `ws://localhost:8787/ws` (set the plugin/API URL
  accordingly).
- Typecheck: `npm run typecheck` (uses `tsc --noEmit`).
- Secrets are Worker secrets, not in the repo. The dealer token also lives in
  the plugin config (and previously in
  `%TEMP%\opencode\pr_dealer_token.txt` for tests).

### Backend change checklist
1. `npm run typecheck`.
2. If storage/schema/behaviour must apply to the live DO: bump `DO_VERSION`.
3. `npm run deploy`.
4. `curl /health` to confirm the version.
5. If `DO_VERSION` changed, tell the user to **create a new game**.
6. Update `docs/ARCHITECTURE.md` if the protocol/state changed.

## 4. Web deploy runbook

- Pages deploys automatically on pushes to `main` touching `web/**`.
- Build locally: `cd web; npx tsc --noEmit; npx vite build`.
- Preview: `npx vite preview --port 4173 --strictPort` then open
  `http://localhost:4173/#/demo` (the demo needs no backend).
- After a deploy, hard-refresh (Ctrl+F5); the asset hash changes.
- The web is versionless — no plugin release is needed for web-only changes.

## 5. Troubleshooting (symptom → check)

| Symptom | Likely cause / check |
|---|---|
| Dalamud "Load failed" on update | Manifest vs repo version mismatch. Check the zip manifest vs `repo.json`; use 4-part versions + tag guard. |
| Installer doesn't see a new version | Combined repo raw CDN cache (~5 min); check GitHub API contents. |
| Plugin connects but commands do nothing | Whitelist (`AuthorizedDealers`), `authorization:` line in `dalamud.log`. |
| Tell does nothing | Same data center? Display name instead of `Name@World`? Check `[chat] sent …` / game chat error. |
| `/random` not captured | Chat line format changed; look for `[dice] unparsed roll line:` and update the regex. |
| Trade not credited | `[trade] opened/unmatched` lines; partner name mismatch; throttle 800ms. |
| Bets rejected | Per-spot/total/balance limits; message in the status or `dealer_error`. |
| `too many bet changes — slow down` | Client is flooding; the server allows 30 bet mutations per 2s per socket. |
| Player gets `dealer only` | Expected — dealer operations are role-gated. If the dealer sees it, the socket authenticated as player/spectator; reconnect. |
| Balance updates instantly | Reveal guard broken: web `renderBalance`/`renderPlayers`, in-game party delay (23.9s). |
| Wheel tiny / not resizing | Canvas sized while hidden; ResizeObserver + `refreshSize()` on table show. |
| Corner/split hover glow off | Zones must be centered on the gap middle (cell edge + 4px for an 8px gap). |
| Chip clipped under neighboring cells | A `z-index` on a hovered/covered `.cell` (stacking context) or a `filter`; keep cells `z-index: auto`, zones at 8, chips at 6. |
| Dealer page doesn't auto-login | URL token missing/stripped; token in localStorage; `#/dealer/{game}/{token}`. |
| Demo not working | `#/demo`; hard-refresh; check browser console. |

Logs: `%APPDATA%\XIVLauncher\dalamud.log` — grep `[wire]`, `[dice]`, `[trade]`,
`[chat]`, `authorization`, `dealer_error`. Wire logging is verbose by default;
turn off `VerboseWireLog` in `/pr config`.

## 6. Rollback

| Want | Do |
|---|---|
| Previous plugin release | `git checkout vX.Y.Z.0` (or the `backup-pre-revamp` tag), push; retag/release as needed |
| Previous web visuals | `backup-pre-revamp` tag / `backups/web-0.6.6-backup.zip` |
| Previous backend | Revert the code **and bump `DO_VERSION`** (resets storage) |
| Combined repo listing | Regenerate from the per-plugin `repo.json`s or hand-patch the version |

## 7. Secrets / tokens

- Worker secret: `DEALER_TOKEN` (wrangler secret).
- Plugin config: `%APPDATA%\XIVLauncher\pluginConfigs\PrivateRoulette.json`
  (`DealerToken`). Never commit it.
- Player keys: per-player, embedded in invite links; players never receive
  other players' keys.
- The `Open dealer page` button puts the dealer token in the URL **hash**; it
  is never sent to GitHub (fragments are client-side) and the page removes it
  from the address bar after reading.
