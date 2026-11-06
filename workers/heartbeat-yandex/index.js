const STORAGE = "https://storage.yandexcloud.net";
const METADATA = "http://169.254.169.254/computeMetadata/v1/instance/service-accounts/default/token";
const OBJECT_KEY = "clients.json";
const TTL_SECONDS = 60 * 60 * 24 * 90;
const MAX_CLIENTS = 500;

const BUCKET = process.env.BUCKET || "";
const READ_TOKEN = process.env.READ_TOKEN || "";

let cachedToken = { value: "", expires: 0 };

async function iamToken() {
  if (cachedToken.value && Date.now() < cachedToken.expires) {
    return cachedToken.value;
  }
  const response = await fetch(METADATA, { headers: { "Metadata-Flavor": "Google" } });
  if (!response.ok) {
    throw new Error("metadata " + response.status);
  }
  const data = await response.json();
  const expires = Date.now() + Math.max(60, (data.expires_in || 3600) - 60) * 1000;
  cachedToken = { value: data.access_token, expires };
  return cachedToken.value;
}

async function loadClients() {
  const token = await iamToken();
  const response = await fetch(`${STORAGE}/${BUCKET}/${OBJECT_KEY}`, {
    headers: { "X-YaCloud-SubjectToken": token },
  });
  if (response.status === 404) {
    return {};
  }
  if (!response.ok) {
    throw new Error("storage get " + response.status);
  }
  const data = await response.json();
  return data && typeof data === "object" ? data : {};
}

async function saveClients(clients) {
  const token = await iamToken();
  const response = await fetch(`${STORAGE}/${BUCKET}/${OBJECT_KEY}`, {
    method: "PUT",
    headers: { "X-YaCloud-SubjectToken": token, "Content-Type": "application/json" },
    body: JSON.stringify(clients),
  });
  if (!response.ok) {
    throw new Error("storage put " + response.status);
  }
}

function reply(status, payload) {
  return {
    statusCode: status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Access-Control-Allow-Origin": "*" },
    body: JSON.stringify(payload),
  };
}

function parseBody(event) {
  const raw = event.isBase64Encoded ? Buffer.from(event.body || "", "base64").toString("utf8") : event.body || "";
  try {
    return JSON.parse(raw || "{}");
  } catch {
    return null;
  }
}

exports.handler = async (event) => {
  const method = (event.httpMethod || "GET").toUpperCase();
  const path = (event.path || "/").replace(/\/+$/, "") || "/";
  if (method === "OPTIONS") {
    return reply(204, {});
  }

  if (path === "/heartbeat" && method === "POST") {
    const data = parseBody(event);
    if (!data) {
      return reply(400, { error: "bad json" });
    }
    const key = String(data.instance_id || "").trim().slice(0, 64);
    if (!key) {
      return reply(400, { error: "no instance_id" });
    }
    const now = Math.floor(Date.now() / 1000);
    const clients = await loadClients();
    const previous = clients[key] || {};
    clients[key] = {
      instance_id: key,
      username: String(data.username || previous.username || "").slice(0, 32),
      version: String(data.version || "").slice(0, 32),
      loader: String(data.loader || "").slice(0, 32),
      last_seen: now,
      first_seen: previous.first_seen || now,
    };
    const fresh = Object.values(clients)
      .filter((item) => now - (item.last_seen || 0) < TTL_SECONDS)
      .sort((a, b) => (b.last_seen || 0) - (a.last_seen || 0))
      .slice(0, MAX_CLIENTS);
    const trimmed = {};
    for (const item of fresh) {
      trimmed[item.instance_id] = item;
    }
    await saveClients(trimmed);
    return reply(200, { ok: true });
  }

  if (path === "/clients" && method === "GET") {
    const token = (event.queryStringParameters || {}).token || "";
    if (!READ_TOKEN || token !== READ_TOKEN) {
      return reply(403, { error: "forbidden" });
    }
    const clients = await loadClients();
    const items = Object.values(clients).sort((a, b) => (b.last_seen || 0) - (a.last_seen || 0));
    return reply(200, { clients: items, count: items.length, updated_at: Math.floor(Date.now() / 1000) });
  }

  return reply(200, { ok: true, service: "craftunity-heartbeat" });
};
