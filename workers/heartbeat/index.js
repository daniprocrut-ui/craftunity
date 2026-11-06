export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "");
    const cors = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET,POST,OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, X-Token",
    };
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: cors });
    }

    if (path.endsWith("/heartbeat") && request.method === "POST") {
      let data;
      try {
        data = await request.json();
      } catch {
        return json({ error: "bad json" }, 400, cors);
      }
      const key = String(data.instance_id || "").trim().slice(0, 64);
      if (!key) {
        return json({ error: "no instance_id" }, 400, cors);
      }
      const now = Math.floor(Date.now() / 1000);
      const prev = (await env.CLIENTS.get(key, "json")) || {};
      const record = {
        instance_id: key,
        username: String(data.username || prev.username || "").slice(0, 32),
        icon: String(data.icon || prev.icon || "").slice(0, 256),
        version: String(data.version || prev.version || "").slice(0, 32),
        loader: String(data.loader || prev.loader || "").slice(0, 32),
        started: Number(data.started || prev.started || 0) || 0,
        game_started: Number(data.game_started || prev.game_started || 0) || 0,
        in_game: Boolean(data.in_game),
        last_seen: now,
        first_seen: prev.first_seen || now,
      };
      if (Array.isArray(data.content)) {
        record.content = data.content
          .slice(0, 400)
          .filter((item) => item && typeof item === "object")
          .map((item) => ({
            title: String(item.title || "").slice(0, 80),
            kind: String(item.kind || "mod").slice(0, 20),
            disabled: Boolean(item.disabled),
          }));
        record.content_at = now;
      } else if (Array.isArray(prev.content)) {
        record.content = prev.content;
        record.content_at = prev.content_at || 0;
      }
      await env.CLIENTS.put(key, JSON.stringify(record), { expirationTtl: 60 * 60 * 24 * 90 });
      return json({ ok: true, storage: "cloudflare-kv" }, 200, cors);
    }

    if (path.endsWith("/clients") && request.method === "GET") {
      const token = url.searchParams.get("token") || request.headers.get("X-Token") || "";
      if (!env.READ_TOKEN || token !== env.READ_TOKEN) {
        return json({ error: "forbidden" }, 403, cors);
      }
      const listed = await env.CLIENTS.list({ limit: 1000 });
      const clients = [];
      for (const item of listed.keys) {
        const value = await env.CLIENTS.get(item.name, "json");
        if (value) clients.push(value);
      }
      clients.sort((a, b) => (b.last_seen || 0) - (a.last_seen || 0));
      return json(
        {
          clients,
          count: clients.length,
          storage: "cloudflare-kv",
          updated_at: Math.floor(Date.now() / 1000),
        },
        200,
        cors,
      );
    }

    if (path.endsWith("/manifest") || path.endsWith("/manifest.json")) {
      return Response.redirect(
        "https://raw.githubusercontent.com/daniprocrut-ui/craftunity/main/docs/manifest.json",
        302,
      );
    }

    return json({ ok: true, service: "craftunity-heartbeat", storage: "cloudflare-kv" }, 200, cors);
  },
};

function json(payload, status, headers) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { ...headers, "Content-Type": "application/json; charset=utf-8" },
  });
}
