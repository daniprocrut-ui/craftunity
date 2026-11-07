const $ = (sel) => document.querySelector(sel);
let manifest = null;

async function api(path, options = {}) {
  const response = await fetch(path, options);
  if (response.status === 401) { location.href = "/admin/login"; return {}; }
  const text = await response.text();
  let data = {};
  try { data = text ? JSON.parse(text) : {}; } catch (e) { data = { error: text }; }
  if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
  return data;
}

function toast(message, bad = false) {
  const el = $("#toast");
  el.textContent = message;
  el.className = "toast on" + (bad ? " bad" : "");
  clearTimeout(el._timer);
  el._timer = setTimeout(() => { el.className = "toast"; }, 4000);
}

function fillForm(data) {
  manifest = data;
  $("#mVersion").value = data.version || "";
  $("#mGame").value = (data.game && data.game.version) || "1.20.1";
  $("#mLoader").value = (data.game && data.game.loader) || "fabric";
  $("#mLoaderVersion").value = (data.game && data.game.loader_version) || "";
  $("#mMessage").value = data.message || "";
  $("#mHost").value = (data.server && data.server.host) || "craftunity.mcsh.io";
  $("#mPort").value = (data.server && data.server.port) || 25565;
  $("#mJvm").value = ((data.jvm && data.jvm.args) || []).join(" ");
  renderMods();
}

function renderMods() {
  const box = $("#modList");
  const list = (manifest && manifest.mods) || [];
  if (!list.length) {
    box.innerHTML = `<div class="empty">Модпак пуст — лаунчер поставит только клиент</div>`;
    return;
  }
  box.innerHTML = list.map((mod, index) => `
    <div class="list-item">
      <div class="grow">
        <div class="name">${escapeHtml(mod.title || mod.slug || mod.project_id)}</div>
        <div class="meta">${escapeHtml(mod.project_id || mod.slug || "")}</div>
      </div>
      <button class="btn small danger" data-remove="${index}">Убрать</button>
    </div>`).join("");
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));
}

function collect() {
  const mods = (manifest && manifest.mods) || [];
  return {
    ...(manifest || {}),
    version: $("#mVersion").value.trim(),
    message: $("#mMessage").value.trim(),
    game: {
      version: $("#mGame").value.trim(),
      loader: $("#mLoader").value,
      loader_version: $("#mLoaderVersion").value.trim(),
    },
    server: {
      host: $("#mHost").value.trim(),
      port: parseInt($("#mPort").value, 10) || 25565,
      auto_join: true,
    },
    jvm: {
      args: $("#mJvm").value.split(/\s+/).filter(Boolean),
      java: "",
    },
    mods,
  };
}

async function load() {
  try {
    const data = await api("/api/admin/manifest");
    fillForm(data.manifest);
    $("#saveHint").textContent = "Загружено. Изменения публикуются кнопкой «Сохранить и опубликовать».";
    const clients = await api("/api/admin/clients");
    renderClients(clients.clients || []);
  } catch (err) {
    toast(err.message, true);
  }
}

function renderClients(clients) {
  const grid = $("#playersGrid");
  renderPlayersStats(clients);
  if (!clients.length) {
    grid.innerHTML = `<div class="empty" style="grid-column:1/-1">Пока нет данных — подключённые лаунчеры появятся здесь</div>`;
    return;
  }
  grid.innerHTML = clients.map((client) => playerCard(client, clients, Date.now() / 1000)).join("");
}

function renderPlayersStats(clients) {
  const box = $("#playersStats");
  if (!box) return;
  const now = Date.now() / 1000;
  const online = clients.filter((client) => isOnline(client, now)).length;
  const total = clients.reduce((sum, client) => sum + playSeconds(client, now), 0);
  box.innerHTML = `
    <span class="stat"><b>${clients.length}</b> игроков</span>
    <span class="stat online"><b>${online}</b> в сети</span>
    <span class="stat"><b>${formatPlaytime(total)}</b> суммарное время</span>`;
}

function isOnline(client, now) {
  return Boolean(client.last_seen) && now - client.last_seen < 300;
}

function playSeconds(client, now = Date.now() / 1000) {
  let total = Number(client.play_seconds) || 0;
  if (client.session_start && now - Number(client.last_seen || 0) < 300) {
    total += Math.max(0, now - Number(client.session_start));
  }
  return total;
}

function formatPlaytime(seconds) {
  const minutes = Math.floor(seconds / 60);
  if (seconds < 60) return seconds > 0 ? "< 1 мин" : "0 мин";
  if (minutes < 60) return `${minutes} мин`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  if (hours < 100) return rest ? `${hours} ч ${rest} мин` : `${hours} ч`;
  return `${Math.floor(hours / 24)} д ${hours % 24} ч`;
}

function playShare(client, all, now) {
  const max = Math.max(...all.map((item) => playSeconds(item, now)), 1);
  return Math.round((playSeconds(client, now) / max) * 100);
}

function relativeSeen(client, now) {
  if (!client.last_seen) return "ещё не заходил";
  const diff = Math.max(0, Math.round(now - client.last_seen));
  if (diff < 60) return "только что";
  if (diff < 3600) return `${Math.floor(diff / 60)} мин назад`;
  if (diff < 86400) return `${Math.floor(diff / 3600)} ч назад`;
  return new Date(client.last_seen * 1000).toLocaleDateString("ru-RU");
}

function playerCard(client, all, now) {
  const name = client.username || "Без аккаунта";
  const online = isOnline(client, now);
  const inGame = online && Boolean(client.in_game);
  const status = inGame
    ? "В игре"
    : online
      ? "В лаунчере"
      : "Не в сети";
  const icon = client.icon
    ? `<img src="${escapeHtml(client.icon)}" alt="" loading="lazy" referrerpolicy="no-referrer" data-fallback="${styleAttr(initials(name))}">`
    : fallbackAvatar(name);
  const content = Array.isArray(client.content) ? client.content : null;
  const mods = content ? content.filter((item) => item && item.kind === "mod") : [];
  const play = playSeconds(client, now);
  const share = Array.isArray(all) ? playShare(client, all, now) : 0;
  const label = inGame ? "в игре всего · сессия идёт" : "в игре всего";
  return `
    <article class="player">
      <div class="player-top">
        <div class="player-avatar">${icon}<span class="dot ${inGame ? "ingame" : online ? "on" : "off"}"></span></div>
        <div class="player-id">
          <div class="player-name">${escapeHtml(name)}</div>
          <div class="player-sub">${status} · ${escapeHtml(relativeSeen(client, now))}</div>
        </div>
      </div>
      <div class="player-time">
        <div class="player-time-value">${formatPlaytime(play)}</div>
        <div class="player-time-label">${label}</div>
      </div>
      <div class="player-bar"><i style="width:${share}%"></i></div>
      <div class="player-mods">
        ${
          mods.length
            ? mods.slice(0, 4).map((mod) => `<span class="chip${mod.disabled ? " off" : ""}" title="${escapeHtml(mod.title || "")}">${escapeHtml(mod.title || "мод")}</span>`).join("") +
              (mods.length > 4 ? `<span class="chip more">+${mods.length - 4}</span>` : "")
            : `<span class="player-mods-empty">${client.version ? `не передавали состав модов · ${escapeHtml(client.version)}` : "не передавали состав модов"}</span>`
        }
      </div>
    </article>`;
}

function initials(name) {
  return (name || "?").replace(/[^\p{L}\p{N}]/gu, "").slice(0, 2).toUpperCase() || "?";
}

function fallbackAvatar(name) {
  const digest = hashName(name);
  const a = 20 + (digest % 36);
  return `<span class="player-avatar-fallback" data-fallback="${styleAttr(initials(name))}" style="--h:${a}">${escapeHtml(initials(name))}</span>`;
}

function styleAttr(text) {
  return escapeHtml(text);
}

function hashName(name) {
  let hash = 0;
  const value = String(name || "?");
  for (let i = 0; i < value.length; i += 1) hash = (hash * 31 + value.charCodeAt(i)) % 360;
  return hash;
}

async function save(publish = true) {
  try {
    if (publish) {
      const data = await api("/api/admin/manifest", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ manifest: collect() }),
      });
      fillForm(data.manifest);
      $("#saveHint").innerHTML = `Опубликовано: сборка <b>${data.manifest.version}</b>, подпись обновлена.`;
      toast("Манифест опубликован");
    } else {
      toast("Изменения не сохранены — нажмите «Сохранить и опубликовать»", true);
    }
  } catch (err) {
    toast(err.message, true);
  }
}

$("#saveBtn").addEventListener("click", () => save(true));
$("#reloadBtn").addEventListener("click", load);
$("#logoutBtn").addEventListener("click", async () => {
  await api("/api/admin/logout", { method: "POST" });
  location.href = "/admin/login";
});
$("#addModBtn").addEventListener("click", () => {
  const id = $("#modId").value.trim();
  if (!id) return toast("Укажите project_id или slug мода", true);
  if (!manifest.mods) manifest.mods = [];
  manifest.mods.push({ project_id: id, slug: id, title: $("#modTitle").value.trim() || id });
  $("#modId").value = "";
  $("#modTitle").value = "";
  renderMods();
});
$("#modList").addEventListener("click", (e) => {
  const index = e.target.dataset.remove;
  if (index === undefined) return;
  manifest.mods.splice(Number(index), 1);
  renderMods();
});

load();
setInterval(async () => {
  try {
    const clients = await api("/api/admin/clients");
    renderClients(clients.clients || []);
  } catch (e) { /* ignore */ }
}, 5000);
