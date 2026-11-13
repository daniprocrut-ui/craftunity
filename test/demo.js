const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

const VIEW_META = {
  play: ["Играть", "Craftunity — один клиент, один сервер"],
  mods: ["Контент", "Моды, ресурспаки и шейдеры с установкой в один клик"],
  installed: ["Установлено", "Ваши моды, ресурспаки и шейдеры"],
  files: ["Файлы", "Свои файлы в папке игры"],
  skin: ["Скин", "Загрузка и предпросмотр скина"],
  account: ["Аккаунт", "Оффлайн-профиль или ely.by"],
  settings: ["Настройки", "Java, память, манифест обновлений"],
};

const THEMES = [
  { id: "dark", name: "Тёмная" },
  { id: "light", name: "Светлая" },
  { id: "midnight", name: "Полуночная" },
  { id: "forest", name: "Лесная" },
  { id: "sunset", name: "Закатная" },
  { id: "ocean", name: "Океанская" },
  { id: "sakura", name: "Сакура" },
  { id: "neon", name: "Неоновая" },
  { id: "graphite", name: "Графитовая" },
  { id: "crimson", name: "Багровая" },
  { id: "coffee", name: "Кофейная" },
  { id: "sand", name: "Песочная" },
  { id: "lavender", name: "Лавандовая" },
  { id: "arctic", name: "Арктическая" },
];

const CATALOG = {
  mod: [
    ["fabric-api", "Fabric API", "Lightweight and modular API providing common hooks and intercompatibility measures", "#c8c8c8", "4.8M", true],
    ["sodium", "Sodium", "A high-performance rendering engine replacement for Minecraft", "#2f9e44", "31.2M", true],
    ["iris", "Iris Shaders", "A modern shader pack loader for Minecraft intended to be compatible with existing OptiFine sets", "#7a5cf0", "15.6M", true],
    ["entityculling", "Entity Culling", "Using async path-tracing to hide Block/Entities that are not visible", "#4b5563", "19.8M", false],
    ["sodium-extra", "Sodium Extra", "Features that shouldn't be in Sodium", "#a3c644", "12.1M", true],
    ["lithium", "Lithium", "No-compromises game logic and performance optimization mod", "#5ec26a", "22.4M", false],
    ["indium", "Indium", "Sodium addon providing support for the Fabric Rendering API", "#8b5cf6", "9.7M", true],
    ["modmenu", "Mod Menu", "Adds a mod menu to view a list of your installed mods", "#3b82f6", "26.9M", false],
    ["jade", "Jade", "Information about what you're looking at, at the top of your screen", "#f59e0b", "11.3M", false],
    ["ferritecore", "FerriteCore", "Reduces memory usage", "#ef4444", "8.2M", false],
    ["cloth-config", "Cloth Config API", "Configuration library for Minecraft mods", "#64748b", "14.5M", false],
    ["architectury", "Architectury API", "An intermediary api aimed at easing development of multiplatform mods", "#0ea5e9", "10.9M", false],
  ],
  resourcepack: [
    ["faithful", "Faithful 32x", "A classic 32x texture pack preserving the vanilla look", "#d97706", "6.1M", false],
    ["fresh-animations", "Fresh Animations", "Enhanced vanilla entity animations", "#22c55e", "4.4M", false],
    ["stay-true", "Stay True", "Reduces the noise level of vanilla textures", "#a855f7", "2.7M", false],
    ["bare-bones", "Bare Bones", "A simplistic resource pack with clean textures", "#e11d48", "1.9M", false],
  ],
  shader: [
    ["complementary", "Complementary Reimagined", "A well-balanced shader with realistic lighting", "#16a34a", "9.3M", false],
    ["bliss", "Bliss Shaders", "Lush lighting with soft shadows and volumetric fog", "#f97316", "5.2M", false],
    ["photon", "Photon Shader", "High quality shader with gorgeous lighting and fast performance", "#eab308", "3.8M", false],
    ["bsl", "BSL Shaders", "A shader pack with colorful lighting and water", "#0ea5e9", "7.6M", false],
  ],
};

const INSTALLED = [
  ["cobblemon", "Cobblemon", "мод", "1.3.0 · 26.2", "A Fabric side-service mod adding Pokémon-style creatures", "#f59e0b"],
  ["polymer", "Polymer", "мод", "0.17.5 · 26.2", "Library for creating better server side content", "#a16207"],
  ["universal-graves", "Universal Graves", "мод", "3.12.0 · 26.2", "Customisable grave mod for Fabric", "#7c7c7c"],
  ["repurposed-structures", "Repurposed Structures", "мод", "7.7.6 · 26.2", "Adds more variations of vanilla structures", "#166534"],
  ["midnightlib", "MidnightLib", "мод", "1.1.0 · 26.2-fabric", "Common Library providing a lightweight configuration system", "#6366f1"],
  ["forge-config-api", "Forge Config API Port", "мод", "26.2-1.1", "NeoForge's & Forge's config systems provided to other modding ecosystems", "#d6a02c"],
  ["travellers-backpack", "Traveller's Backpack", "мод", "1.1.7 · 26.2-fabric", "Unique and upgradeable backpacks with customisation", "#8b5a2b"],
  ["craft-config", "Craft Config Lib", "мод", "1.1.7 · 26.2-fabric", "A client side configuration library", "#22c55e"],
  ["sodium", "Sodium", "мод", "0.5.11 · 26.2", "High-performance rendering engine", "#2f9e44"],
  ["iris", "Iris Shaders", "мод", "1.7.3 · 26.2", "Modern shader pack loader", "#7a5cf0"],
  ["fabric-api", "Fabric API", "мод", "0.92.2 · 26.2", "Core Fabric hooks and APIs", "#c8c8c8"],
  ["modmenu", "Mod Menu", "мод", "7.2.2 · 26.2", "List of installed mods", "#3b82f6"],
  ["complementary", "Complementary Reimagined", "шейдер", "r5.2", "Well-balanced shader with realistic lighting", "#16a34a"],
  ["faithful", "Faithful 32x", "ресурспак", "r3", "32x texture pack preserving vanilla look", "#d97706"],
  ["fresh-animations", "Fresh Animations", "ресурспак", "1.9", "Enhanced vanilla entity animations", "#22c55e"],
  ["craftunity-pack", "Craftunity Pack", "модпак", "1.20.1", "Сборка сервера Craftunity", "#ff8a3d"],
];

function initials(name) {
  return (name || "?").replace(/[^A-Za-zА-Яа-я0-9]/g, "").slice(0, 1).toUpperCase();
}

function tileColor(seed) {
  return seed || "#ff8a3d";
}

function modCard([slug, title, desc, color, downloads, installed]) {
  const cls = installed ? "btn small ghost" : "btn small";
  const label = installed ? "Установлен" : "Установить";
  return `<div class="mod-card">
    <div class="mod-icon" style="background:${tileColor(color)}22; color:${tileColor(color)}">${initials(title)}</div>
    <div class="mod-name">${title}</div>
    <div class="mod-meta">Modrinth</div>
    <div class="mod-desc">${desc}</div>
    <div class="mod-meta">${downloads} загрузок</div>
    <button class="${cls}" data-demo-play disabled>${label}</button>
  </div>`;
}

function installedRow([slug, title, kind, version, desc, color]) {
  return `<div class="list-item">
    <div class="mod-icon small" style="background:${tileColor(color)}22; color:${tileColor(color)}">${initials(title)}</div>
    <div class="grow">
      <div class="name">${title} <span class="tag">${kind}</span></div>
      <div class="meta">${version}</div>
      <div class="meta">${desc}</div>
    </div>
  </div>`;
}

function toast(message) {
  const el = $("#toast");
  el.textContent = message;
  el.className = "toast on";
  clearTimeout(el._timer);
  el._timer = setTimeout(() => { el.className = "toast"; }, 3000);
}

function applyTheme(id) {
  const known = THEMES.some((t) => t.id === id) ? id : "dark";
  document.documentElement.dataset.theme = known;
  const label = (THEMES.find((t) => t.id === known) || THEMES[0]).name;
  const toggle = $("#themeToggle");
  if (toggle) toggle.textContent = label;
  $$("#themeTiles button").forEach((b) => b.classList.toggle("active", b.dataset.theme === known));
}

function showView(id) {
  const meta = VIEW_META[id] || VIEW_META.play;
  $$("#nav button").forEach((b) => b.classList.toggle("active", b.dataset.view === id));
  $$(".view").forEach((s) => s.classList.toggle("active", s.id === "view-" + id));
  $("#viewTitle").textContent = meta[0];
  $("#viewSub").textContent = meta[1];
  if (id === "skin") drawSkin();
}

function drawAvatar() {
  const canvas = $("#avatarCanvas");
  if (!canvas) return;
  const w = 340, h = 300;
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  const bg = ctx.createLinearGradient(0, 0, 0, h);
  bg.addColorStop(0, "#1b2230");
  bg.addColorStop(1, "#0d1017");
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, w, h);
  const cx = w / 2;
  ctx.fillStyle = "#3b4a63";
  ctx.fillRect(cx - 26, 150, 52, 62);
  ctx.fillStyle = "#ff6b35";
  ctx.fillRect(cx - 58, 150, 30, 58);
  ctx.fillRect(cx + 28, 150, 30, 58);
  ctx.fillStyle = "#2d3748";
  ctx.fillRect(cx - 26, 212, 22, 52);
  ctx.fillRect(cx + 4, 212, 22, 52);
  ctx.fillStyle = "#e0b089";
  ctx.fillRect(cx - 42, 74, 84, 78);
  ctx.fillStyle = "#2b2118";
  ctx.fillRect(cx - 42, 74, 84, 18);
  ctx.fillStyle = "#1f2937";
  ctx.fillRect(cx - 22, 112, 14, 8);
  ctx.fillRect(cx + 8, 112, 14, 8);
  ctx.fillStyle = "#7c4a33";
  ctx.fillRect(cx - 14, 138, 28, 5);
}

function drawSkin() {
  const canvas = $("#skinCanvas");
  if (!canvas) return;
  const w = 260, h = 340;
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  const bg = ctx.createLinearGradient(0, 0, 0, h);
  bg.addColorStop(0, "#1b2230");
  bg.addColorStop(1, "#0d1017");
  ctx.fillStyle = bg;
  ctx.fillRect(0, 0, w, h);
  const cx = w / 2;
  const slim = $("#skinModel") && $("#skinModel").value === "slim";
  const armW = slim ? 14 : 20;
  ctx.fillStyle = "#2f6fd0";
  ctx.fillRect(cx - 26, 150, 52, 62);
  ctx.fillStyle = "#8a5a3b";
  ctx.fillRect(cx - 26 - armW, 150, armW, 58);
  ctx.fillRect(cx + 26, 150, armW, 58);
  ctx.fillStyle = "#24406b";
  ctx.fillRect(cx - 26, 212, 22, 52);
  ctx.fillRect(cx + 4, 212, 22, 52);
  ctx.fillStyle = "#e0b089";
  ctx.fillRect(cx - 42, 74, 84, 78);
  ctx.fillStyle = "#2b2118";
  ctx.fillRect(cx - 42, 74, 84, 18);
  ctx.fillStyle = "#1f2937";
  ctx.fillRect(cx - 22, 112, 14, 8);
  ctx.fillRect(cx + 8, 112, 14, 8);
}

function boot() {
  drawAvatar();
  drawSkin();
  applyTheme("dark");
  showView("play");

  $("#modResults").innerHTML = CATALOG.mod.map(modCard).join("");
  $("#installedMods").innerHTML = INSTALLED.map(installedRow).join("");

  $$("#nav button").forEach((b) => b.addEventListener("click", () => showView(b.dataset.view)));

  document.addEventListener("click", (event) => {
    const theme = event.target.closest("#themeTiles button");
    if (theme) { applyTheme(theme.dataset.theme); return; }
    const kind = event.target.closest("#contentTabs button");
    if (kind) {
      $$("#contentTabs button").forEach((b) => b.classList.remove("active"));
      kind.classList.add("active");
      const list = CATALOG[kind.dataset.kind] || CATALOG.mod;
      $("#modResults").innerHTML = list.map(modCard).join("");
      $("#modFilter").textContent = "Показано " + list.length + " из " + list.length;
      return;
    }
    const mode = event.target.closest("#contentViewModes button");
    if (mode) {
      $$("#contentViewModes button").forEach((b) => b.classList.remove("active"));
      mode.classList.add("active");
      return;
    }
    const filter = event.target.closest("#installedTabs button");
    if (filter) {
      $$("#installedTabs button").forEach((b) => b.classList.remove("active"));
      filter.classList.add("active");
      const f = filter.dataset.filter;
      const rows = f === "all" ? INSTALLED : INSTALLED.filter((r) => r[2] === f);
      $("#installedMods").innerHTML = rows.map(installedRow).join("");
      return;
    }
    const demo = event.target.closest("[data-demo-play]");
    if (demo) {
      event.preventDefault();
      toast("Демо-версия: запуск и установка отключены");
      return;
    }
  });

  if ($("#skinModel")) $("#skinModel").addEventListener("change", drawSkin);
  if ($("#themeToggle")) {
    $("#themeToggle").addEventListener("click", () => {
      const cur = document.documentElement.dataset.theme || "dark";
      const idx = THEMES.findIndex((t) => t.id === cur);
      applyTheme(THEMES[(idx + 1) % THEMES.length].id);
    });
  }
}

document.addEventListener("DOMContentLoaded", boot);
