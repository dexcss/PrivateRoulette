# Private Roulette

Private, whitelisted dealer plugin for hosting gil roulette tables on the
SimpleRoulette service — with a UI that doesn't look like a spreadsheet.
Clean-room rebuild: no code from the original plugin, just its documented wire
protocol (see the reverse-engineering notes in `SimpleRoulette-RE/PROTOCOL.md`).

**Access control:** only characters on the authorized dealer list can use this
plugin. The socket connection is never opened for anyone else, and the list is
editable in `/pr config`.

## Features

- Provably-fair control spins: dealer rolls `/random` (0–999), the server
  combines it with the game's control number. Every result is verified locally
  against `sha256("{dice}:{control_number}") → seed % 37` and badged ✓.
- Dealer console: create/join games, players, balances, chips, ready states,
  request spin → dice → result, clear bets, rebet, ledger refresh.
- Automatic dice capture from chat (EN/DE/FR/JP roll messages).
- Chat announcements with `$placeholder` templates.
- Verifiable-fairness display per spin; table history; chat.

Commands: `/pr` (dealer console), `/pr config` (settings).

## Building

```
dotnet build -c Release
```

Releases are cut by pushing a `v*` tag; the Release workflow produces
`PrivateRoulette.zip` with a generated Dalamud manifest.
