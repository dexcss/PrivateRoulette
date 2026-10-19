// Private Roulette API — Cloudflare Worker entry.
// Routes WebSocket connections to the per-game TableRoom Durable Object.

import { Env, TableRoom } from "./table";

export { TableRoom };

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return Response.json({ ok: true, service: "private-roulette-api" });
    }

    // WebSocket upgrade: /ws?game=<gameId>
    // All connections route to a single Durable Object ("global") so players
    // and the dealer share the same state. The TableRoom validates dealer
    // tokens / player keys after "hello".
    if (url.pathname === "/ws") {
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
        return new Response("expected websocket", { status: 426 });
      }
      const id = env.TABLE.idFromName("global");
      const stub = env.TABLE.get(id);
      return stub.fetch(request);
    }

    return new Response("private roulette api", { status: 200 });
  },
};
