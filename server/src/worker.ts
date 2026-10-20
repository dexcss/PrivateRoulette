// Private Roulette API — Cloudflare Worker entry.
// Routes WebSocket connections to the per-game TableRoom Durable Object.

import { Env, TableRoom } from "./table";

export { TableRoom };

// Durable Object instances run the Worker code version they were started with
// until they are evicted. Bumping this suffix on deploys that must take effect
// immediately spins up a fresh instance (storage is per-name, so bumping
// resets the games list — only do it when that is acceptable).
const DO_VERSION = "v10";

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return Response.json({ ok: true, service: "private-roulette-api", version: DO_VERSION });
    }

    // WebSocket upgrade: /ws
    // All connections route to a single Durable Object so players and the
    // dealer share the same state. The TableRoom validates dealer tokens /
    // player keys after "hello".
    if (url.pathname === "/ws") {
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
        return new Response("expected websocket", { status: 426 });
      }
      const id = env.TABLE.idFromName(`global-${DO_VERSION}`);
      const stub = env.TABLE.get(id);
      return stub.fetch(request);
    }

    return new Response("private roulette api", { status: 200 });
  },
};
