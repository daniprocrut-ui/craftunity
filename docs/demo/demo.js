const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

const VIEW_META = {
  play: ["Играть", "Craftunity — один лаунчер, один сервер"],
  mods: ["Контент", "Моды, ресурспаки и шейдеры с установкой в один клик"],
  installed: ["Установлено", "Ваши моды, ресурспаки и шейдеры"],
  files: ["Файлы", "Свои файлы в папке игры"],
  skin: ["Скин", "Загрузка и предпросмотр скина"],
  account: ["Аккаунт", "Оффлайн-профиль или ely.by"],
  chat: ["Чат", "Общий чат, объявления, новости и личные сообщения"],
  settings: ["Настройки", "Java, память, темы, манифест обновлений"],
};

// Палитры взяты из c#launcher/Themes.cs — те же 14 тем и тот же порядок.
const THEMES = [
  ["dark", "Тёмная", "#090b14", "#141a2c", "#1c2338", "#e9eefb", "#8b95b2", "#fb923c", "#1c0f02", "#2a3350"],
  ["light", "Светлая", "#f3f4f8", "#ffffff", "#eef1f7", "#171a24", "#626a80", "#ea580c", "#ffffff", "#d7dbe8"],
  ["midnight", "Полуночная", "#04060e", "#0f1628", "#16203a", "#e6ecfb", "#7f8cad", "#38bdf8", "#04121d", "#22304f"],
  ["forest", "Лесная", "#07100c", "#102018", "#16291f", "#e8f6ed", "#88a695", "#4ade80", "#04140b", "#24402f"],
  ["sunset", "Закатная", "#150811", "#2a1226", "#381a31", "#fdeaf6", "#b48ba6", "#fb7185", "#2a0712", "#4d2545"],
  ["ocean", "Океанская", "#04121b", "#0b2b3d", "#0f3850", "#e3f6fd", "#7fa6b8", "#2dd4bf", "#03201f", "#1b526d"],
  ["sakura", "Сакура", "#1a0f16", "#301a28", "#3d2233", "#fdeaf4", "#b48ea3", "#f472b6", "#2b0a1c", "#57304a"],
  ["neon", "Неоновая", "#050208", "#120a22", "#1a0f30", "#f0e8ff", "#9080b0", "#a855f7", "#16042b", "#2d1a4d"],
  ["graphite", "Графитовая", "#0e1013", "#1d2126", "#262b32", "#eef1f5", "#8b95a1", "#9aa7b8", "#14171b", "#363d46"],
  ["crimson", "Багровая", "#120508", "#261014", "#32161b", "#fde8ec", "#b58a95", "#ef4444", "#240507", "#4a2028"],
  ["coffee", "Кофейная", "#14100c", "#261f18", "#322920", "#f6eee4", "#a8947d", "#c1814a", "#1f1308", "#4a3d2e"],
  ["sand", "Песочная", "#f6efe2", "#fffdf7", "#f3ead9", "#2a2117", "#7d6a52", "#b45309", "#2a1a05", "#dccdb2"],
  ["lavender", "Лавандовая", "#f3f2fb", "#ffffff", "#f0eefb", "#1e1a33", "#6b6490", "#7c3aed", "#ffffff", "#d8d3ef"],
  ["arctic", "Арктическая", "#eef4f8", "#ffffff", "#eaf2f8", "#10222e", "#5e7b8d", "#0284c7", "#ffffff", "#cfdee8"],
];

const CATALOG = {
  mod: [
    ["fabric-api", "Fabric API", "Lightweight and modular API providing common hooks and intercompatibility measures", "#9aa7b8", "4.8M", true],
    ["sodium", "Sodium", "A high-performance rendering engine replacement for Minecraft", "#2f9e44", "31.2M", true],
    ["iris", "Iris Shaders", "Modern shader pack loader compatible with existing OptiFine packs", "#7a5cf0", "15.6M", true],
    ["entityculling", "Entity Culling", "Async path-tracing to hide blocks and entities that are not visible", "#4b5563", "19.8M", false],
    ["lithium", "Lithium", "No-compromises game logic and performance optimization mod", "#5ec26a", "22.4M", false],
    ["indium", "Indium", "Sodium addon providing support for the Fabric Rendering API", "#8b5cf6", "9.7M", true],
    ["modmenu", "Mod Menu", "Adds a mod menu to view a list of your installed mods", "#3b82f6", "26.9M", false],
    ["jade", "Jade", "Information about what you are looking at, at the top of your screen", "#f59e0b", "11.3M", false],
    ["ferritecore", "FerriteCore", "Reduces memory usage", "#ef4444", "8.2M", false],
    ["cloth-config", "Cloth Config API", "Configuration library for Minecraft mods", "#64748b", "14.5M", false],
    ["architectury", "Architectury API", "Intermediary api easing development of multiplatform mods", "#0ea5e9", "10.9M", false],
    ["cobblemon", "Cobblemon", "Fabric side-service mod adding Pokémon-style creatures", "#f59e0b", "3.1M", true],
  ],
  resourcepack: [
    ["faithful", "Faithful 32x", "Classic 32x texture pack preserving the vanilla look", "#d97706", "6.1M", false],
    ["fresh-animations", "Fresh Animations", "Enhanced vanilla entity animations", "#22c55e", "4.4M", false],
    ["stay-true", "Stay True", "Reduces the noise level of vanilla textures", "#a855f7", "2.7M", false],
    ["bare-bones", "Bare Bones", "Simplistic resource pack with clean textures", "#e11d48", "1.9M", false],
  ],
  shader: [
    ["complementary", "Complementary Reimagined", "Well-balanced shader with realistic lighting", "#16a34a", "9.3M", false],
    ["bliss", "Bliss Shaders", "Lush lighting with soft shadows and volumetric fog", "#f97316", "5.2M", false],
    ["photon", "Photon Shader", "High quality shader with gorgeous lighting and fast performance", "#eab308", "3.8M", false],
    ["bsl", "BSL Shaders", "Shader pack with colorful lighting and water", "#0ea5e9", "7.6M", false],
  ],
};

const INSTALLED = [
  ["cobblemon", "Cobblemon", "мод", "1.3.0 · 26.2", "A Fabric side-service mod adding Pokémon-style creatures", "#f59e0b"],
  ["polymer", "Polymer", "мод", "0.17.5 · 26.2", "Library for creating better server side content", "#a16207"],
  ["universal-graves", "Universal Graves", "мод", "3.12.0 · 26.2", "Customisable grave mod for Fabric", "#7c7c7c"],
  ["repurposed-structures", "Repurposed Structures", "мод", "7.7.6 · 26.2", "More variations of vanilla structures", "#166534"],
  ["midnightlib", "MidnightLib", "мод", "1.1.0 · 26.2-fabric", "Lightweight configuration system", "#6366f1"],
  ["forge-config-api", "Forge Config API Port", "мод", "26.2-1.1", "Forge and NeoForge config systems for other ecosystems", "#d6a02c"],
  ["travellers-backpack", "Traveller's Backpack", "мод", "1.1.7 · 26.2-fabric", "Upgradeable backpacks with customisation", "#8b5a2b"],
  ["sodium", "Sodium", "мод", "0.5.11 · 26.2", "High-performance rendering engine", "#2f9e44"],
  ["iris", "Iris Shaders", "мод", "1.7.3 · 26.2", "Modern shader pack loader", "#7a5cf0"],
  ["complementary", "Complementary Reimagined", "шейдер", "r5.2", "Well-balanced shader with realistic lighting", "#16a34a"],
  ["faithful", "Faithful 32x", "ресурспак", "r3", "32x texture pack preserving vanilla look", "#d97706"],
  ["craftunity-pack", "Craftunity Pack", "модпак", "1.20.1", "Сборка сервера Craftunity", "#fb923c"],
];

const CHATS = {
  general: [
    { author: "SonicWave", mine: false, time: "14:02", text: "Кто-нибудь ставил новый шейдер? На 1.20.1 очень бодро.", reacts: [["👍", 3], ["🔥", 1]] },
    { author: "Panda_MC", mine: false, time: "14:05", text: "Да, поставил Complementary через вкладку Контент. Всё в один клик.", image: "скриншот · 1280×720", reacts: [["👍", 5]] },
    { author: "Danipro914", mine: true, time: "14:07", text: "Обновил сборку до 1.5.37 — обновится при следующем запуске.", reacts: [["❤️", 2], ["😄", 1]] },
    { author: "Kotik", mine: false, time: "14:11", text: "У меня клиент скачался, но Java качается второй раз.", edited: true, reacts: [] },
    { author: "Danipro914", mine: true, time: "14:12", text: "Это кэш Java: во вкладке Настройки выключи «Автоматически скачивать Java», если 17 уже стоит.", reacts: [["👍", 1]] },
  ],
  announcements: [
    { author: "Danipro914", mine: true, time: "13:20", text: "Версия лаунчера 1.5.37: чат, реакции на сообщения и картинки из буфера.", reacts: [["🔥", 7], ["👍", 4]] },
    { author: "Danipro914", mine: true, time: "13:40", text: "Сервер переключён на 1.20.1 · Fabric. Все моды подтянутся автоматически.", reacts: [["👍", 3]] },
  ],
  news: [
    { author: "Danipro914", mine: true, time: "12:00", text: "Чат появился в лаунчере: общий чат, объявления и личные сообщения с уведомлениями.", reacts: [["❤️", 9], ["🔥", 5]] },
  ],
  dm: {
    SonicWave: [{ author: "SonicWave", mine: false, time: "14:01", text: "Скинь ссылку на демо, гляну с телефона.", reacts: [] }],
    Panda_MC: [{ author: "Panda_MC", mine: false, time: "13:58", text: "Можешь добавить ресурспак в сборку по умолчанию?", reacts: [] }],
    Kotik: [{ author: "Kotik", mine: false, time: "13:44", text: "А модпак обновится сам или руками переставлять?", reacts: [] }],
  },
};

const state = { view: "play", kind: "mod", filter: "all", room: "general", peer: "", theme: "dark" };

function initials(name) {
  return (name || "?").replace(/[^A-Za-zА-Яа-я0-9]/g, "").slice(0, 1).toUpperCase();
}

function mix(hex, other, amount) {
  const parse = (v) => [1, 3, 5].map((i) => parseInt(v.slice(i, i + 2), 16));
  const a = parse(hex);
  const b = parse(other);
  const out = a.map((v, i) => Math.round(v + (b[i] - v) * amount));
  return "#" + out.map((v) => v.toString(16).padStart(2, "0")).join("");
}

function applyTheme(id) {
  const theme = THEMES.find((t) => t[0] === id) || THEMES[0];
  const [, name, bg, panel, card, text, muted, accent, accentFg, border] = theme;
  const root = document.documentElement.style;
  root.setProperty("--bg", bg);
  root.setProperty("--panel", panel);
  root.setProperty("--card", card);
  root.setProperty("--line", border);
  root.setProperty("--text", text);
  root.setProperty("--muted", muted);
  root.setProperty("--accent", accent);
  root.setProperty("--accent-fg", accentFg);
  root.setProperty("--accent-grad", `linear-gradient(135deg, ${mix(accent, "#ffffff", 0.45)} 0%, ${accent} 55%, ${mix(accent, "#000000", 0.22)} 100%)`);
  const grad = $("#accentGrad");
  if (grad) {
    grad.children[0].setAttribute("stop-color", mix(accent, "#ffffff", 0.45));
    grad.children[1].setAttribute("stop-color", accent);
    grad.children[2].setAttribute("stop-color", mix(accent, "#000000", 0.22));
  }
  document.documentElement.dataset.theme = theme[0];
  state.theme = theme[0];
  const toggle = $("#themeToggle");
  if (toggle) toggle.textContent = name;
  $$("#themeTiles button").forEach((b) => b.classList.toggle("active", b.dataset.theme === theme[0]));
}

function toast(message) {
  const el = $("#toast");
  el.textContent = message;
  el.classList.add("on");
  clearTimeout(el._timer);
  el._timer = setTimeout(() => el.classList.remove("on"), 2600);
}

function modCard([slug, title, desc, color, downloads, installed]) {
  return `<div class="mod">
    <div class="mod-icon" style="background:${color}22;color:${color}">${initials(title)}</div>
    <div class="mod-title">${title}</div>
    <div class="mod-desc">${desc}</div>
    <div class="mod-foot"><span>${downloads}</span>
      <button class="btn small" disabled>${installed ? "Установлен" : "Установить"}</button></div>
  </div>`;
}

function instCard([slug, title, kind, version, desc, color]) {
  return `<div class="inst">
    <div class="mod-icon small" style="background:${color}22;color:${color}">${initials(title)}</div>
    <div class="inst-body">
      <div class="inst-title">${title}</div>
      <div class="inst-meta">${kind} · ${version}</div>
      <div class="inst-desc">${desc}</div>
      <div class="actions" style="margin-top:10px">
        <button class="btn small" disabled>Выключить</button>
        <button class="btn small" disabled>Удалить</button>
      </div>
    </div>
  </div>`;
}

function msgHtml(msg) {
  const reacts = (msg.reacts || []).map(([emoji, count]) =>
    `<button class="react" disabled>${emoji} ${count}</button>`).join("");
  return `<div class="msg">
    <div class="msg-avatar" style="color:var(--accent)">${initials(msg.author)}</div>
    <div class="msg-body">
      <div class="msg-top">
        <span class="msg-author">${msg.author}</span>
        <span class="msg-time">${msg.time}</span>
        ${msg.edited ? '<span class="msg-edited">изменено</span>' : ""}
        ${msg.mine ? '<button class="btn small" disabled style="height:22px;padding:2px 8px;font-size:10px">Изменить</button>' : ""}
        ${msg.mine ? '<button class="btn small" disabled style="height:22px;padding:2px 8px;font-size:10px">Удалить</button>' : ""}
      </div>
      <div class="msg-text">${msg.text}</div>
      ${msg.image ? `<div class="msg-image">${msg.image}</div>` : ""}
      <div class="reacts">
        <button class="react" disabled>👍</button>
        <button class="react" disabled>❤️</button>
        <button class="react" disabled>🔥</button>
        <button class="react" disabled>😄</button>
        <button class="react" disabled>😢</button>
        ${reacts}
      </div>
    </div>
  </div>`;
}

function renderChat() {
  const key = state.peer ? "dm" : state.room;
  const list = state.peer ? (CHATS.dm[state.peer] || []) : CHATS[key];
  $("#chatList").innerHTML = list.map(msgHtml).join("");
  const title = state.peer ? "Личные · " + state.peer
    : state.room === "announcements" ? "Объявления"
    : state.room === "news" ? "Новости" : "Общий чат";
  $("#chatTitle").textContent = title;
  const readOnly = state.room === "news" && !state.peer;
  $("#chatReadNote").hidden = !readOnly;
  $("#chatReadNote").textContent = "Новости публикует только Danipro914.";
  $("#composerRow").style.display = readOnly ? "none" : "grid";
  $("#chatInput").placeholder = "Сообщение в «" + title + "»";
  const feed = document.querySelector(".chat-feed");
  if (feed) feed.scrollTop = feed.scrollHeight;
}

function showView(id) {
  state.view = id;
  const meta = VIEW_META[id] || VIEW_META.play;
  $$("#nav button").forEach((b) => b.classList.toggle("active", b.dataset.view === id));
  $$(".view").forEach((s) => s.classList.toggle("active", s.id === "view-" + id));
  $("#viewTitle").textContent = meta[0];
  $("#viewSub").textContent = meta[1];
  document.querySelector(".app").classList.toggle("compact", id === "chat");
  if (id === "chat") {
    renderChat();
    $("#chatBadge").hidden = true;
  }
  if (id === "skin") drawSkin();
}

function drawSkin() {
  const canvas = $("#skinCanvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const w = canvas.width, h = canvas.height;
  const bg = ctx.createLinearGradient(0, 0, 0, h);
  bg.addColorStop(0, "#1b2230");
  bg.addColorStop(1, "#0d1017");
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, w, h);
  const cx = w / 2;
  const slim = $("#skinModel").value === "slim";
  const armW = slim ? 9 : 13;
  ctx.fillStyle = "#2f6fd0";
  ctx.fillRect(cx - 17, 96, 34, 40);
  ctx.fillStyle = "#8a5a3b";
  ctx.fillRect(cx - 17 - armW, 96, armW, 38);
  ctx.fillRect(cx + 17, 96, armW, 38);
  ctx.fillStyle = "#24406b";
  ctx.fillRect(cx - 17, 136, 14, 34);
  ctx.fillRect(cx + 3, 136, 14, 34);
  ctx.fillStyle = "#e0b089";
  ctx.fillRect(cx - 27, 48, 54, 50);
  ctx.fillStyle = "#2b2118";
  ctx.fillRect(cx - 27, 48, 54, 11);
  ctx.fillStyle = "#1f2937";
  ctx.fillRect(cx - 14, 73, 9, 5);
  ctx.fillRect(cx + 5, 73, 9, 5);
}

function boot() {
  applyTheme("dark");

  THEMES.forEach((tile) => {
    const btn = document.createElement("button");
    btn.dataset.theme = tile[0];
    btn.textContent = tile[1];
    $("#themeTiles").appendChild(btn);
  });

  $("#modResults").innerHTML = CATALOG.mod.map(modCard).join("");
  $("#installedList").innerHTML = INSTALLED.map(instCard).join("");
  renderChat();
  drawSkin();

  $$("#nav button").forEach((b) => b.addEventListener("click", () => showView(b.dataset.view)));

  document.addEventListener("click", (event) => {
    const target = event.target;
    if (target.closest("#themeTiles button")) {
      applyTheme(target.closest("#themeTiles button").dataset.theme);
      return;
    }
    if (target.closest("#themeToggle")) {
      const index = THEMES.findIndex((t) => t[0] === state.theme);
      applyTheme(THEMES[(index + 1) % THEMES.length][0]);
      return;
    }
    const kind = target.closest("#kindTabs button");
    if (kind) {
      $$("#kindTabs button").forEach((b) => b.classList.remove("active"));
      kind.classList.add("active");
      const list = CATALOG[kind.dataset.kind] || CATALOG.mod;
      $("#modResults").innerHTML = list.map(modCard).join("");
      $("#modFilter").textContent = "Показано " + list.length + " " +
        (kind.dataset.kind === "mod" ? "модов" : kind.dataset.kind === "shader" ? "шейдеров" : "ресурспаков") +
        " под текущую версию";
      return;
    }
    const filter = target.closest("#installedTabs button[data-filter], #view-installed .tabs button[data-filter]");
    if (filter) {
      $$("#view-installed .tabs button").forEach((b) => b.classList.remove("active"));
      filter.classList.add("active");
      const f = filter.dataset.filter;
      const rows = f === "all" ? INSTALLED : INSTALLED.filter((r) => r[2] === f);
      $("#installedList").innerHTML = rows.map(instCard).join("");
      return;
    }
    const room = target.closest("#rooms li");
    if (room) {
      state.peer = "";
      state.room = room.dataset.room;
      $$("#rooms li").forEach((li) => li.classList.toggle("active", li === room));
      $$("#peers li").forEach((li) => li.classList.remove("active"));
      renderChat();
      return;
    }
    const peer = target.closest("#peers li");
    if (peer) {
      state.peer = peer.dataset.peer;
      $$("#peers li").forEach((li) => li.classList.toggle("active", li === peer));
      $$("#rooms li").forEach((li) => li.classList.remove("active"));
      renderChat();
      return;
    }
    if (target.closest("#ctClose")) {
      $("#chatToast").hidden = true;
      event.stopPropagation();
      return;
    }
    if (target.closest("#chatToast")) {
      $("#chatToast").hidden = true;
      showView("chat");
      return;
    }
    if (target.closest("button[disabled]")) {
      event.preventDefault();
      toast("Демо-версия: запуск, установка и чат отключены");
      return;
    }
  });

  $("#skinModel").addEventListener("change", drawSkin);
  $("#chatInput").addEventListener("keydown", (event) => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    if (!state.peer && state.room === "news") return;
    toast("Демо-версия: чат работает только в настоящем лаунчере");
  });
}

document.addEventListener("DOMContentLoaded", boot);
