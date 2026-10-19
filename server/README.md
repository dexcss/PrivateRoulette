# Private Roulette — Backend

The dealer-facing plugin and the player-facing web client both talk to this
tiny Cloudflare Worker. One Durable Object (`TableRoom`) per game owns all
authoritative state: players, balances, bets, provable-fairness spins and the
full ledger. Nothing else stores or sees your data.

## One-time setup

```
cd server
npm install
npx wrangler login          # opens your browser
openssl rand -hex 24        # or any long random string — this is your DEALER_TOKEN
npx wrangler secret put DEALER_TOKEN   # paste the value when prompted
npx wrangler deploy
```

The deploy prints your worker URL, e.g. `https://private-roulette-api.<your-subdomain>.workers.dev`.

- **Plugin**: `/pr config` → paste the same token. Set the API URL in
  `PrivateRoulette` config if you renamed the worker.
- **Web client**: open the site, "Server settings", paste
  `wss://private-roulette-api.<your-subdomain>.workers.dev/ws` (saved in localStorage).

Local testing without deploying:

```
npm run dev     # ws://localhost:8787/ws
```

## Protocol (our own — see ../docs)

Envelope: `{"t": "<event>", "d": {...}}` both directions over WebSocket at `/ws`.

Client hello:
- dealer: `{ role: "dealer", token, dealer_name, dealer_world }` — token must
  match the `DEALER_TOKEN` secret.
- player: `{ role: "player", game_id, player_key }`
- spectator: `{ role: "spectator", game_id }`

Events mirror the documented flow: `create_game`, `add_player`,
`adjust_balance`, `request_spin`, `spin` (dice or manual number), `place_bet`,
`remove_bet`, `set_ready`, `cashout`, `send_chat` … and broadcasts
`game_state`, `public_bets`, `spin` (with proof), `round_result`,
`table_history`, `chat_message`.

Fairness (identical formula to the one players already know):
`result = uint32(sha256("{dice_roll}:{control_number}")[0..8]) % 37`

## Cost

Cloudflare free tier covers Workers + Durable Objects; this API is far below
those limits for a venue-sized table.
