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
  const body = $("#clientsBody");
  if (!clients.length) {
    body.innerHTML = `<tr><td colspan="4" class="empty">Пока нет данных</td></tr>`;
    return;
  }
  body.innerHTML = clients.map((client) => {
    const seen = client.last_seen ? new Date(client.last_seen * 1000).toLocaleString("ru-RU") : "—";
    return `<tr>
      <td><code>${escapeHtml((client.instance_id || "").slice(0, 8))}</code></td>
      <td>${escapeHtml(client.username || "—")}</td>
      <td>${escapeHtml(client.version || "—")} / ${escapeHtml(client.loader || "")}</td>
      <td>${seen}</td>
    </tr>`;
  }).join("");
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
