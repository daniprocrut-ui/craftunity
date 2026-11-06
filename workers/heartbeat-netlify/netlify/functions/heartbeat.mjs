import { getStore } from "@netlify/blobs";

const KEY = "clients.json";
const TTL_SECONDS = 60 * 60 * 24 * 90;
const MAX_CLIENTS = 500;
const MANIFEST_URL =
  "https://raw.githubusercontent.com/daniprocrut-ui/craftunity/main/docs/manifest.json";

const DROPBOX_TOKEN = process.env.DROPBOX_TOKEN || "";

function json(status, payload) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Access-Control-Allow-Origin": "*",
      "Cache-Control": "no-store",
    },
  });
}

function store() {
  return getStore({ name: "craftunity-clients", consistency: "strong" });
}

async function load() {
  if (DROPBOX_TOKEN) {
    // Dropbox оставлен только как запасной путь чтения, если токен задан вручную.
    try {
      const response = await fetch("https://content.dropboxapi.com/2/files/download", {
        method: "POST",
        headers: {
          Authorization: `Bearer ${DROPBOX_TOKEN}`,
          "Dropbox-API-Arg": JSON.stringify({
            path: process.env.DROPBOX_USERS_PATH || "/craftunity/users.json",
          }),
        },
      });
      if (!response.ok) {
        throw new Error(`dropbox ${response.status}`);
      }
      const data = await response.json();
      if (data && typeof data === "object") return data;
    } catch {
      // молча падаем на Blobs
    }
  }
  const data = await store().get(KEY, { type: "json" });
  return data && typeof data === "object" ? data : {};
}

async function save(payload) {
  await store().setJSON(KEY, payload);
}

export default async (request) => {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, "");
  const method = request.method.toUpperCase();

  if (method === "OPTIONS") {
    return new Response(null, {
      status: 204,
      headers: {
        "Access-Control-Allow-Origin": "*",
        "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
        "Access-Control-Allow-Headers": "Content-Type, X-Token",
      },
    });
  }

  try {
    if (method === "POST" && path.endsWith("/heartbeat")) {
      let data;
      try {
        data = await request.json();
      } catch {
        return json(400, { error: "bad json" });
      }
      const key = String(data.instance_id || "").trim().slice(0, 64);
      if (!key) {
        return json(400, { error: "no instance_id" });
      }
      const now = Math.floor(Date.now() / 1000);
      const clients = await load();
      const previous = clients[key] || {};
      const record = {
        instance_id: key,
        username: String(data.username || previous.username || "").slice(0, 32),
        icon: String(data.icon || previous.icon || "").slice(0, 256),
        version: String(data.version || previous.version || "").slice(0, 32),
        loader: String(data.loader || previous.loader || "").slice(0, 32),
        started: Number(data.started || previous.started || 0) || 0,
        game_started: Number(data.game_started || previous.game_started || 0) || 0,
        in_game: Boolean(data.in_game),
        last_seen: now,
        first_seen: previous.first_seen || now,
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
      } else if (Array.isArray(previous.content)) {
        record.content = previous.content;
        record.content_at = previous.content_at || 0;
      }
      clients[key] = record;

      const fresh = Object.values(clients)
        .filter((item) => now - (item.last_seen || 0) < TTL_SECONDS)
        .sort((a, b) => (b.last_seen || 0) - (a.last_seen || 0))
        .slice(0, MAX_CLIENTS);
      const trimmed = {};
      for (const item of fresh) {
        trimmed[item.instance_id] = item;
      }
      await save(trimmed);
      return json(200, { ok: true, storage: "blobs" });
    }

    if (method === "GET" && (path.endsWith("/manifest") || path.endsWith("/manifest.json"))) {
      const response = await fetch(MANIFEST_URL, {
        headers: { "User-Agent": "craftunity-heartbeat", "Cache-Control": "no-cache" },
      });
      if (!response.ok) {
        return json(502, { error: `manifest ${response.status}` });
      }
      return new Response(await response.text(), {
        status: 200,
        headers: {
          "Content-Type": "application/json; charset=utf-8",
          "Access-Control-Allow-Origin": "*",
          "Cache-Control": "no-store",
        },
      });
    }

    if (method === "GET" && path.endsWith("/clients")) {
      const expected = process.env.READ_TOKEN || "";
      const token = url.searchParams.get("token") || request.headers.get("x-token") || "";
      if (!expected || token !== expected) {
        return json(403, { error: "forbidden" });
      }
      const clients = await load();
      const items = Object.values(clients).sort((a, b) => (b.last_seen || 0) - (a.last_seen || 0));
      return json(200, {
        clients: items,
        count: items.length,
        storage: "blobs",
        updated_at: Math.floor(Date.now() / 1000),
      });
    }

    return json(200, { ok: true, service: "craftunity-heartbeat", storage: "blobs" });
  } catch (error) {
    return json(502, { error: String(error && error.message ? error.message : error) });
  }
};
