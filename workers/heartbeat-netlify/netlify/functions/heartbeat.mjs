import { getStore } from "@netlify/blobs";

const KEY = "clients.json";
const TTL_SECONDS = 60 * 60 * 24 * 90;
const MAX_CLIENTS = 500;
const MANIFEST_URL =
  "https://raw.githubusercontent.com/daniprocrut-ui/craftunity/main/docs/manifest.json";

const DROPBOX_TOKEN = process.env.DROPBOX_TOKEN || "";

const CHAT_KEY = "chat.json";
const CHAT_MAX = 500;
const CHAT_TTL_SECONDS = 60 * 60 * 24 * 30;
const CHAT_MAX_LEN = 500;
const CHAT_MIN_GAP = 3;

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

function chatStore() {
  return getStore({ name: "craftunity-chat", consistency: "strong" });
}

async function loadChat() {
  const data = await chatStore().get(CHAT_KEY, { type: "json" });
  const messages = data && Array.isArray(data.messages) ? data.messages : [];
  const now = Math.floor(Date.now() / 1000);
  return messages.filter((item) => now - Number(item.at || 0) < CHAT_TTL_SECONDS);
}

async function saveChat(messages) {
  await chatStore().setJSON(CHAT_KEY, { messages: messages.slice(-CHAT_MAX) });
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

    if (method === "POST" && path.endsWith("/message")) {
      let data;
      try {
        data = await request.json();
      } catch {
        return json(400, { error: "bad json" });
      }
      const instance = String(data.instance_id || "").trim().slice(0, 64);
      const text = String(data.text || "").replace(/[\u0000-\u001f]/g, " ").trim().slice(0, CHAT_MAX_LEN);
      if (!instance || !text) {
        return json(400, { error: "no instance_id or text" });
      }
      const now = Math.floor(Date.now() / 1000);
      const messages = await loadChat();
      const last = messages[messages.length - 1];
      if (last && last.instance_id === instance && now - Number(last.at || 0) < CHAT_MIN_GAP) {
        return json(429, { error: "too fast" });
      }
      const clients = await load();
      const known = clients[instance] || {};
      const message = {
        id: (last ? Number(last.id || 0) : 0) + 1,
        instance_id: instance,
        username: String(data.username || known.username || "Гость").trim().slice(0, 32) || "Гость",
        icon: String(data.icon || known.icon || "").slice(0, 256),
        text,
        at: now,
      };
      messages.push(message);
      await saveChat(messages);
      return json(200, { ok: true, message });
    }

    if (method === "GET" && path.endsWith("/messages")) {
      const since = Number(url.searchParams.get("since") || 0) || 0;
      const messages = await loadChat();
      const fresh = since > 0 ? messages.filter((item) => Number(item.id || 0) > since) : messages;
      return json(200, {
        messages: fresh,
        last_id: messages.length ? Number(messages[messages.length - 1].id || 0) : 0,
        count: messages.length,
      });
    }

    return json(200, { ok: true, service: "craftunity-heartbeat", storage: "blobs" });
  } catch (error) {
    return json(502, { error: String(error && error.message ? error.message : error) });
  }
};
