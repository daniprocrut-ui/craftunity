const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

const state = {
  overview: null,
  polling: null,
  results: [],
  installed: [],
  installedFilter: "all",
  contentKind: "mod",
  contentView: "tiles",
  modalProject: null,
  modalGallery: [],
  lbImages: [],
  lbIndex: 0,
  lbZoom: 1,
  lbX: 0,
  lbY: 0,
  modalOriginal: "",
  modalTranslated: "",
  modalShowOriginal: false,
  modQuery: '',
  modOffset: 0,
  modTotal: 0,
  searching: false,
  skinImage: null,
  skinDataUrl: null,
};

const CONTENT_LABELS = {
  mod: {
    one: "мод",
    many: "модов",
    title: "Установка модов",
    hint: "Моды берутся из каталога Modrinth и ставятся в папку <code>mods</code> выбранной версии.",
    placeholder: "Название мода, например Sodium или JEI",
  },
  resourcepack: {
    one: "ресурспак",
    many: "ресурспаков",
    title: "Ресурспаки",
    hint: "Ресурспаки из Modrinth и свои .zip попадают в папку <code>resourcepacks</code>.",
    placeholder: "Название ресурспака, например Faithful",
  },
  shader: {
    one: "шейдер",
    many: "шейдеров",
    title: "Шейдеры",
    hint: "Шейдеры ставятся в <code>shaderpacks</code>. Для их работы лаунчер сам ставит Iris.",
    placeholder: "Название шейдера, например Complementary",
  },
};

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

function applyTheme(theme, reduceMotion) {
  const known = THEMES.some((item) => item.id === theme) ? theme : "dark";
  document.documentElement.dataset.theme = known;
  if (typeof reduceMotion === "boolean") {
    document.documentElement.classList.toggle("no-motion", reduceMotion);
  }
  const label = (THEMES.find((item) => item.id === known) || THEMES[0]).name;
  const toggle = $("#themeToggle");
  if (toggle) toggle.textContent = label;
  $$("#themeTiles button").forEach((btn) =>
    btn.classList.toggle("active", btn.dataset.theme === known));
}

async function setTheme(theme) {
  applyTheme(theme);
  try {
    await api("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ theme }),
    });
  } catch (err) {
    toast(err.message, true);
  }
}

async function api(path, options = {}) {
  const response = await fetch(path, options);
  const text = await response.text();
  let data = {};
  try { data = text ? JSON.parse(text) : {}; } catch (e) { data = { error: text }; }
  if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
  return data;
}

async function confirmDependencyRemoval(projectId, name) {
  let payload = {};
  try {
    const response = await fetch("/api/mods/remove", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ project_id: projectId }),
    });
    payload = await response.json();
  } catch (err) {
    toast(err.message, true);
    return;
  }
  if (!payload || !payload.needs_confirm) {
    toast((payload && payload.error) || "Не удалось удалить", true);
    return;
  }
  const list = (payload.dependents || []).map((item) => "• " + (item.title || item.project_id)).join("\n");
  const answer = confirm(
    `«${name}» нужно другим модам:\n\n${list}\n\n` +
    "Если удалить, эти моды перестанут работать.\nУдалить всё равно?"
  );
  if (!answer) return;
  try {
    await api("/api/mods/remove", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ project_id: projectId, force: true }),
    });
    toast("Удалено, зависимые моды могут не работать");
    loadInstalled();
  } catch (err) { toast(err.message, true); }
}

function toast(message, bad = false) {
  const el = $("#toast");
  el.textContent = message;
  el.className = "toast on" + (bad ? " bad" : "");
  clearTimeout(el._timer);
  el._timer = setTimeout(() => { el.className = "toast"; }, 4200);
}

function fmt(value) {
  if (value >= 1e6) return (value / 1e6).toFixed(1) + " млн";
  if (value >= 1e3) return (value / 1e3).toFixed(1) + " тыс.";
  return String(value ?? 0);
}

function switchView(name) {
  $$("#nav button").forEach((b) => b.classList.toggle("active", b.dataset.view === name));
  $$(".view").forEach((v) => v.classList.toggle("active", v.id === "view-" + name));
  $("#viewTitle").textContent = VIEW_META[name][0];
  $("#viewSub").textContent = VIEW_META[name][1];
  if (name === "mods" && !state.results.length) searchMods("");
  if (name === "installed") loadInstalled();
  if (name === "files") loadFiles();
  if (name === "skin") renderSkin();
}

/* ---------------------------------------------------------------- skin draw */

function drawFromSkin(source, ctx, w, h, scale) {
  const unit = Math.floor(Math.min(w / 16, h / 24));
  const ox = Math.round((w - unit * 16) / 2);
  const oy = Math.round((h - unit * 23) / 2);
  const part = (sx, sy, sw, sh, dx, dy, dw, dh) => {
    ctx.drawImage(source, sx, sy, sw, sh, ox + dx * unit, oy + dy * unit, dw * unit, dh * unit);
  };
  ctx.clearRect(0, 0, w, h);
  ctx.imageSmoothingEnabled = false;
  part(8, 8, 8, 8, 4, 0, 8, 8);          // head
  part(20, 20, 8, 12, 4, 8, 8, 12);       // body
  part(44, 20, 4, 12, 0, 8, 4, 12);       // right arm
  part(36, 52, 4, 12, 12, 8, 4, 12);      // left arm
  part(4, 20, 4, 12, 4, 20, 4, 12);       // right leg
  part(20, 52, 4, 12, 8, 20, 4, 12);      // left leg
  // overlays
  part(40, 8, 8, 8, 4, 0, 8, 8);
  part(20, 36, 8, 12, 4, 8, 8, 12);
  part(44, 36, 4, 12, 0, 8, 4, 12);
  part(52, 52, 4, 12, 12, 8, 4, 12);
  part(4, 36, 4, 12, 4, 20, 4, 12);
  part(4, 52, 4, 12, 8, 20, 4, 12);
}

function renderSkin() {
  const canvas = $("#skinCanvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (!state.skinImage) {
    ctx.fillStyle = "#151925";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = "#5c6478";
    ctx.font = "13px Segoe UI";
    ctx.textAlign = "center";
    ctx.fillText("Скин не загружен", canvas.width / 2, canvas.height / 2);
    return;
  }
  drawFromSkin(state.skinImage, ctx, canvas.width, canvas.height);
}

function renderAvatar() {
  const canvas = $("#avatarCanvas");
  const ctx = canvas.getContext("2d");
  ctx.imageSmoothingEnabled = false;
  ctx.clearRect(0, 0, 8, 8);
  if (state.skinImage) {
    ctx.drawImage(state.skinImage, 8, 8, 8, 8, 0, 0, 8, 8);
    ctx.drawImage(state.skinImage, 40, 8, 8, 8, 0, 0, 8, 8);
  } else {
    const gradient = ctx.createLinearGradient(0, 0, 8, 8);
    gradient.addColorStop(0, "#6ee7a8");
    gradient.addColorStop(1, "#7c5cff");
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, 8, 8);
  }
}

function loadSkinImage(dataUrl) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => { state.skinImage = img; renderSkin(); renderAvatar(); resolve(img); };
    img.onerror = () => resolve(null);
    img.src = dataUrl;
  });
}

/* ------------------------------------------------------------------ render */

function accountLabel(account) {
  if (!account) return "Аккаунт не выбран";
  return account.type === "ely" ? "Аккаунт ely.by" : "Оффлайн-аккаунт";
}

function greetingText(date = new Date()) {
  const hour = date.getHours();
  if (hour >= 5 && hour < 12) return "Доброе утро";
  if (hour >= 12 && hour < 18) return "Добрый день";
  if (hour >= 18 && hour < 23) return "Добрый вечер";
  return "Доброй ночи";
}

function refreshGreeting() {
  const node = $("#heroName");
  if (!node) return;
  const account = (state.overview || {}).account;
  const nickname = account ? account.username : "Гость";
  node.textContent = `${greetingText()}, ${nickname}`;
}

function renderHolidayDecor(event, still) {
  const layer = $("#holidayLayer");
  if (!layer) return;
  layer.innerHTML = "";
  const count = Math.min(30, Number(event.count) || 18);
  const shape = event.shape || "dot";
  for (let index = 0; index < count; index += 1) {
    const piece = document.createElement("span");
    piece.className = `holiday-piece shape-${shape}${still ? " still" : ""}`;
    const size = (shape === "pumpkin" ? 14 : 5) + Math.round(Math.random() * 7);
    piece.style.left = `${Math.round(Math.random() * 100)}%`;
    piece.style.width = `${size}px`;
    piece.style.height = `${size}px`;
    piece.style.opacity = (0.25 + Math.random() * 0.45).toFixed(2);
    piece.style.setProperty("--holiday-top", `${Math.round(Math.random() * 95)}%`);
    if (!still) {
      piece.style.animationDelay = `${(Math.random() * 9).toFixed(2)}s`;
      piece.style.animationDuration = `${(10 + Math.random() * 10).toFixed(2)}s`;
    }
    layer.appendChild(piece);
  }
}

function applyHoliday(event, settings) {
  const root = document.documentElement;
  const enabled = (settings || {}).holiday_theme !== false;
  const chip = $("#holidayChip");
  if (!event || !enabled) {
    root.removeAttribute("data-holiday");
    const layer = $("#holidayLayer");
    if (layer) layer.innerHTML = "";
    if (chip) chip.remove();
    return;
  }
  root.dataset.holiday = String(event.id || "");
  renderHolidayDecor(event, Boolean((settings || {}).reduce_motion));
  const chips = $("#heroChips");
  if (!chips) return;
  let badge = $("#holidayChip");
  if (!badge) {
    badge = document.createElement("span");
    badge.className = "chip holiday-chip";
    badge.id = "holidayChip";
    chips.appendChild(badge);
  }
  badge.textContent = String(event.title || "");
}

function renderOverview(data) {
  state.overview = data;
  const account = data.account;
  const manifest = data.manifest || {};
  const game = manifest.game || {};
  const server = manifest.server || {};

  refreshGreeting();
  $("#heroType").textContent = accountLabel(account);
  $("#launcherVer").textContent = data.launcher_version || "—";
  $("#statusVersion").textContent = "v" + (data.launcher_version || "1.0.0");

  const chips = [];
  if (game.version) chips.push(`<span class="chip">Версия <b>${game.version}</b></span>`);
  if (game.loader) chips.push(`<span class="chip">Загрузчик <b>${game.loader}</b></span>`);
  if (game.loader_version) chips.push(`<span class="chip">Loader <b>${game.loader_version}</b></span>`);
  if (manifest.version) chips.push(`<span class="chip">Сборка <b>${manifest.version}</b></span>`);
  chips.push(`<span class="chip">Модов <b>${data.installed_mods ?? 0}</b></span>`);
  const cached = data.manifest_source === "cache";
  if (cached) chips.push(`<span class="chip" style="border-color:rgba(255,193,7,.4)">Манифест <b>из кеша</b></span>`);
  $("#heroChips").innerHTML = chips.join("");
  applyHoliday(data.holiday, data.settings || {});

  const warn = $("#manifestWarn");
  if (warn) {
    const manifestMissing = !manifest.version || data.manifest_source === "none" || Boolean(data.manifest_error);
    if (manifestMissing) {
      const localVersion = game.version || (data.settings || {}).game_version || "1.20.1";
      warn.style.display = "block";
      warn.innerHTML = `<b style="color:#ffd479">Версия не подтверждена сервером.</b> `
        + `${escapeHtml(data.manifest_error || "манифест не получен")}<br>`
        + `Запустится локальная сборка <b>${escapeHtml(localVersion)}</b> · Fabric. `
        + `Проверьте адрес манифеста в настройках — он лежит в Dropbox или на GitHub Pages.`;
    } else {
      warn.style.display = "none";
    }
  }

  const host = server.host || data.settings.server_host || "craftunity.mcsh.io";
  const port = server.port || data.settings.server_port || 25565;
  $("#serverPill").innerHTML = `<span>Сервер</span><b>${host}</b>`;
  $("#serverCard").innerHTML = `
    <div style="font-size:15px;color:var(--text);font-weight:600">${host}:${port}</div>
    <div style="margin-top:8px">${manifest.message || "Добро пожаловать на Craftunity!"}</div>
    <div style="margin-top:8px;font-size:11.5px">Автовход в игру: ${server.auto_join === false ? "выключен" : "включён"}</div>`;

  const snap = data.status || {};
  const running = snap.running;
  $("#playBtn").disabled = !account || running || snap.status === "installing" || snap.status === "starting";
  $("#playBtn").textContent = running ? "Игра запущена" : "Играть";
  $("#stopBtn").style.display = running ? "inline-block" : "none";
  $("#playHint").textContent = !account
    ? "Выберите аккаунт, чтобы начать"
    : running
      ? "Minecraft работает"
      : "Клиент обновится автоматически";

  const prog = $("#progress");
  const busy = ["installing", "starting"].includes(snap.status);
  prog.classList.toggle("on", busy || (snap.status === "error" && snap.progress > 0));
  if (busy) {
    $("#progressBar").style.width = Math.round((snap.progress || 0) * 100) + "%";
    $("#progressStage").textContent = snap.message || snap.stage || "Подготовка…";
    $("#progressPct").textContent = Math.round((snap.progress || 0) * 100) + "%";
  }

  const stateBox = $("#clientState");
  const rows = [
    ["Манифест", data.manifest_error ? "недоступен" : manifest.version ? "подписан и принят" : "не загружен"],
    ["Клиент", snap.status === "ready" ? "готов" : busy ? "обновляется" : snap.status === "running" ? "работает" : "не проверен"],
    ["Java", data.java || "не найдена"],
    ["Папка игры", data.game_dir || "—"],
  ];
  stateBox.innerHTML = rows.map(([k, v]) => `
    <div class="list-item"><div class="grow"><div class="name">${k}</div><div class="meta">${v}</div></div></div>`).join("");

  const fetchedAt = data.manifest_fetched_at
    ? new Date(data.manifest_fetched_at * 1000).toLocaleString("ru-RU")
    : null;
  $("#statusText").textContent = snap.message
    || (cached && fetchedAt ? `Сохранённая версия от ${fetchedAt}` : "Готово");
  $("#netDot").className = "dot" + (data.manifest_error ? " bad" : "");
  $("#netText").textContent = data.manifest_error
    ? "Манифест недоступен"
    : cached
      ? "Манифест из кеша"
      : "Сервер доступен";

  const settings = data.settings || {};
  applyTheme(settings.theme || "dark", Boolean(settings.reduce_motion));
  const nextView = settings.content_view === "list" ? "list" : "tiles";
  if (nextView !== state.contentView) {
    state.contentView = nextView;
    $$("#contentViewModes button").forEach((btn) =>
      btn.classList.toggle("active", btn.dataset.contentView === state.contentView));
    renderMods();
    renderInstalled();
  }
  $("#optReduceMotion").checked = Boolean(settings.reduce_motion);
  $("#optAutoJava").checked = settings.auto_java !== false;
  $("#optAutoJoin").checked = settings.auto_join !== false;
  if ($("#optHoliday")) $("#optHoliday").checked = settings.holiday_theme !== false;
  if (document.activeElement !== $("#setMemory")) $("#setMemory").value = settings.memory_mb || 4096;
  if (document.activeElement !== $("#setJava")) $("#setJava").value = settings.java_path || "";
  if ($("#setCurseforgeKey") && document.activeElement !== $("#setCurseforgeKey")) {
    $("#setCurseforgeKey").value = settings.curseforge_key || "";
  }
  if ($("#curseforgeState")) {
    $("#curseforgeState").textContent = settings.curseforge_key
      ? "CurseForge включён: выдача из двух каталогов, дубли ставятся с Modrinth"
      : "CurseForge выключен: без ключа поиск идёт только по Modrinth";
  }
  $("#setVersion").value = `${game.version || settings.game_version} / ${game.loader || settings.loader}`;
  $("#setServer").value = `${host}:${port}`;
  if ($("#manifestAuto")) {
    $("#manifestAuto").textContent = manifest.version
      ? (settings.manifest_url || "найден автоматически")
      : "Пока не найден — используется сохранённая копия или локальные настройки";
  }
  $("#manifestInfo").innerHTML = manifest.version
    ? `Сборка <b>${manifest.version}</b> · обновлена ${manifest.updated_at || "—"} · модов в модпаке: ${(manifest.mods || []).length}`
      + (cached && fetchedAt ? `<br>Сервер недоступен — версия взята из сохранённой копии (${fetchedAt}).` : "")
    : data.manifest_error || "Манифест не загружен";

  const log = $("#logBox");
  if (log.classList.contains("on")) {
    log.innerHTML = (snap.log || []).map((line) =>
      `<div class="${/error|exception|could not|failed/i.test(line) ? "err" : ""}">${escapeHtml(line)}</div>`).join("");
    log.scrollTop = log.scrollHeight;
  }
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"]/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[ch]));
}

async function refresh() {
  try {
    const data = await api("/api/overview");
    renderOverview(data);
    if (data.skin) {
      state.skinDataUrl = data.skin.data_url;
      if (!state.skinImage) await loadSkinImage(data.skin.data_url);
    } else if (!state.skinImage) {
      renderSkin();
    }
    if (data.installed_mods !== state.installed.length) loadInstalled();
  } catch (err) {
    $("#statusText").textContent = "Нет связи с лаунчером: " + err.message;
  }
}

/* ------------------------------------------------------------------- mods */

const MODS_PAGE = 24;

function installedIds() {
  return new Set((state.installed || []).map((mod) => mod.project_id));
}

function fallbackIcon(img) {
  const fallback = img.dataset.fallback || "";
  if (fallback && img.getAttribute("src") !== fallback) {
    img.setAttribute("src", fallback);
    img.dataset.fallback = "";
    return;
  }
  img.style.visibility = "hidden";
}

function renderMods() {
  const box = $("#modResults");
  const more = $("#modMoreWrap");
  if (box) box.className = "mods-grid mode-" + (state.contentView || "tiles");
  if (!state.results.length) {
    box.innerHTML = `<div class="empty">Попробуйте другой запрос</div>`;
    more.style.display = "none";
    return;
  }
  const done = installedIds();
  box.innerHTML = state.results.map((hit) => {
    const installed = done.has(hit.id);
    return `
      <div class="mod" data-open="${hit.id}">
        <img src="${hit.icon_hd || hit.icon || ""}" alt="" data-fallback="${hit.icon || ""}" onerror="fallbackIcon(this)">
        <div class="mod-body">
          <div class="title">${escapeHtml(hit.title)} <span class="chip" style="margin-left:6px">${hit.source === "curseforge" ? "CurseForge" : "Modrinth"}</span></div>
          <div class="desc">${escapeHtml(hit.description || "")}</div>
          ${hit.author ? `<div class="author">автор: ${escapeHtml(hit.author)}</div>` : ""}
          <div class="foot">
            <span class="stat">${fmt(hit.downloads)} загрузок</span>
            <button class="btn small" data-install="${hit.id}" ${installed ? "disabled" : ""}>
              ${installed ? "Установлен" : "Установить"}
            </button>
          </div>
        </div>
      </div>`;
  }).join("");
  $("#modFilter").textContent = state.results.length
    ? `Показано ${state.results.length} из ${state.modTotal || state.results.length} ${CONTENT_LABELS[state.contentKind].many} под текущую версию`
    : "";
  more.style.display = state.results.length < (state.modTotal || state.results.length) ? "flex" : "none";
}

function syncUploadButton(kind) {
  const button = $("#uploadPack");
  const input = $("#packFile");
  if (!button || !input) return;
  const isMod = kind === "mod";
  button.textContent = isMod ? "Добавить .jar" : "Добавить .zip";
  button.title = isMod
    ? "Добавить свой .jar в папку модов"
    : "Добавить свой .zip: ресурспак или архив шейдера";
  input.accept = isMod ? ".jar" : ".zip";
}

function switchContentKind(kind) {
  if (!CONTENT_LABELS[kind]) kind = "mod";
  state.contentKind = kind;
  syncUploadButton(kind);
  $$("#contentTabs button").forEach((btn) => btn.classList.toggle("active", btn.dataset.kind === kind));
  const label = CONTENT_LABELS[kind];
  $("#contentTitle").textContent = label.title;
  $("#contentHint").innerHTML = label.hint;
  $("#modQuery").placeholder = label.placeholder;
  state.results = [];
  state.modOffset = 0;
  state.modTotal = 0;
  $("#modMoreWrap").style.display = "none";
  searchMods("");
}

async function searchMods(query, append = false) {
  const box = $("#modResults");
  if (state.searching) return;
  if (!append) {
    state.modQuery = query;
    state.results = [];
    state.modOffset = 0;
    state.modTotal = 0;
    box.innerHTML = `<div class="empty"><span class="spinner"></span> Поиск модов…</div>`;
    $("#modMoreWrap").style.display = "none";
  } else {
    const btn = $("#modMore");
    btn.disabled = true;
    btn.textContent = "Загружаю…";
  }
  state.searching = true;
  try {
    const data = await api("/api/mods/search", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        query: state.modQuery,
        limit: MODS_PAGE,
        offset: state.modOffset,
        sort: $("#modSort") ? $("#modSort").value : "relevance",
        kind: state.contentKind,
      }),
    });
    const hits = data.hits || [];
    state.results = append ? state.results.concat(hits) : hits;
    state.modOffset = state.results.length;
    state.modTotal = data.total_hits || data.total || state.results.length;
    if (!state.results.length) {
      $("#modFilter").textContent = `Ничего не найдено среди ${CONTENT_LABELS[state.contentKind].many} под текущую версию`;
    }
    renderMods();
  } catch (err) {
    if (append) {
      toast(err.message, true);
    } else {
      box.innerHTML = `<div class="empty">Ошибка: ${escapeHtml(err.message)}</div>`;
    }
  } finally {
    state.searching = false;
    const btn = $("#modMore");
    btn.disabled = false;
    btn.textContent = "Показать ещё";
  }
}

async function loadInstalled() {
  try {
    const data = await api("/api/mods/installed");
    state.installed = data.mods || [];
    renderInstalled();
  } catch (err) {
    toast(err.message, true);
  } finally {
    if (state.results.length) renderMods();
  }
}

function packModIds() {
  const list = (((state.overview || {}).manifest || {}).mods) || [];
  const ids = new Set();
  list.forEach((entry) => {
    const id = String(entry.project_id || entry.slug || "");
    if (id) ids.add(id);
  });
  return ids;
}

function renderInstalled() {
  const box = $("#installedMods");
  if (!box) return;
  const filter = state.installedFilter || "all";
  const packIds = packModIds();
  const onlyPack = filter === "modpack";
  const items = state.installed.filter((item) => {
    const kind = item.kind || "mod";
    const inPack = kind === "mod" && packIds.has(String(item.project_id || ""));
    if (onlyPack) return inPack;
    return filter === "all" || kind === filter;
  });
  box.className = "installed-grid mode-" + (state.contentView || "tiles");
  const hint = $("#installedHint");
  if (hint) {
    hint.textContent = onlyPack
      ? `Модов в модпаке: ${items.length} · состав задаётся сервером, удалить их нельзя`
      : `Всего: ${state.installed.length}` + (filter === "all" ? "" : ` · показано: ${items.length}`);
  }
  if (!items.length) {
    box.innerHTML = onlyPack
      ? `<div class="empty">Модпак ещё не установлен — запустите игру или нажмите «Играть»</div>`
      : `<div class="empty">Пока ничего не установлено</div>`;
    return;
  }
  box.innerHTML = items.map((mod) => {
    const label = CONTENT_LABELS[mod.kind] || CONTENT_LABELS.mod;
    const tracked = !mod.untracked && !String(mod.project_id || "").startsWith("file:");
    const openAttr = mod.project_id ? ` data-open="${escapeHtml(mod.project_id)}"` : "";
    const locked = (mod.kind || "mod") === "mod" && packIds.has(String(mod.project_id || ""));
    const removeAttrs = mod.untracked
      ? `data-remove-file="1" data-kind="${mod.kind || "mod"}" data-filename="${escapeHtml(mod.filename || "")}"`
      : `data-remove="${escapeHtml(mod.project_id || "")}"`;
    const disabled = Boolean(mod.disabled);
    const toggleAttrs = mod.untracked
      ? `data-toggle-file="1" data-kind="${mod.kind || "mod"}" data-filename="${escapeHtml(mod.filename || "")}" data-enabled="${disabled ? "0" : "1"}"`
      : `data-toggle="${escapeHtml(mod.project_id || "")}" data-enabled="${disabled ? "0" : "1"}"`;
    const toggleHtml = `<button class="btn small${disabled ? "" : " ghost"}" ${toggleAttrs}>${disabled ? "Включить" : "Выключить"}</button>`;
    const actions = locked
      ? `<span class="chip" title="Мод приходит из модпака и обновляется сервером">из модпака</span>`
      : disabled
        ? toggleHtml
        : toggleHtml + `<button class="btn small danger" ${removeAttrs}>Удалить</button>`;
    const stateChip = disabled ? `<span class="chip off">выключен</span>` : "";
    const missing = Array.isArray(mod.missing_dependencies) ? mod.missing_dependencies : [];
    const warnHtml = missing.length
      ? `<span class="dep-warn" tabindex="0">!<span class="dep-tip">` +
        `<b>Мод может не работать</b><br>Не хватает: ${escapeHtml(missing.join(", "))}` +
        `</span></span>`
      : "";
    const warnClass = missing.length ? " has-warning" : "";
    const canInstallDeps = missing.length && !String(mod.project_id || "").startsWith("file:");
    const depHtml = canInstallDeps
      ? `<button class="btn small primary" data-install-deps="${escapeHtml(mod.project_id || "")}">Установить зависимости</button>`
      : "";
    const removeHtml = `<div class="installed-actions">${actions}${depHtml}</div>`;
    const title = escapeHtml(mod.title || mod.slug || "файл");
    const remoteIcon = mod.icon_hd || mod.icon || "";
    const iconSrc = tracked ? `/api/icon/${encodeURIComponent(mod.project_id)}` : remoteIcon;
    const icon = iconSrc
      ? `<img class="installed-icon" src="${iconSrc}" alt="" data-fallback="${escapeHtml(remoteIcon)}" onerror="fallbackIcon(this)">`
      : `<div class="installed-icon placeholder">${escapeHtml((mod.title || mod.slug || "?").trim().charAt(0).toUpperCase())}</div>`;
    if (state.contentView === "list") {
      return `
        <div class="list-item installed-item${disabled ? " is-off" : ""}${warnClass}"${openAttr}>
          ${icon}
          <div class="grow">
            <div class="name">${title} <span class="chip" style="margin-left:6px">${label.one}</span> ${stateChip} ${warnHtml}</div>
            <div class="meta">${escapeHtml(mod.version || "")} ${mod.filename ? "· " + escapeHtml(mod.filename) : ""}</div>
            ${mod.description ? `<div class="installed-desc">${escapeHtml(mod.description)}</div>` : ""}
          </div>
          ${removeHtml}
        </div>`;
    }
    return `
      <div class="installed-card installed-item${disabled ? " is-off" : ""}${warnClass}"${openAttr}>
        ${icon}
        <div class="installed-title">${title}</div>
        <div class="installed-meta"><span class="chip">${label.one}</span> ${escapeHtml(mod.version || "")} ${stateChip} ${warnHtml}</div>
        ${mod.description ? `<div class="installed-desc">${escapeHtml(mod.description)}</div>` : ""}
        ${removeHtml}
      </div>`;
  }).join("");
}

/* ---------------------------------------------------------------- project */

function stripMarkdown(text) {
  return String(text || "")
    .replace(/```[\s\S]*?```/g, " ")
    .replace(/!\[[^\]]*\]\([^)]*\)/g, " ")
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
    .replace(/<[^>]+>/g, " ")
    .replace(/[#>*_`~]/g, "")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

async function installProject(projectId, kind, button) {
  if (button) {
    button.disabled = true;
    button.textContent = "Ставлю…";
  }
  try {
    const result = await api("/api/mods/install", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ project_id: projectId, kind }),
    });
    toast(`Установлен ${(CONTENT_LABELS[kind] || CONTENT_LABELS.mod).one}: ` + (result.mod?.title || ""));
    await loadInstalled();
    return result;
  } catch (err) {
    toast(err.message, true);
    if (button) {
      button.disabled = false;
      button.textContent = "Установить";
    }
    return null;
  }
}

function closeProject() {
  const modal = $("#projectModal");
  if (modal) modal.classList.remove("on");
}

async function openProject(projectId) {
  const modal = $("#projectModal");
  if (!modal || !projectId) return;
  state.modalProject = null;
  state.modalOriginal = "";
  state.modalTranslated = "";
  state.modalShowOriginal = false;
  modal.classList.add("on");
  $("#modalBody").innerHTML = `<div class="empty"><span class="spinner"></span> Загружаю страницу…</div>`;
  try {
    const data = await api(`/api/project/${encodeURIComponent(projectId)}`);
    state.modalProject = data.project;
    renderProject(data.project);
    translateDescription(data.project);
  } catch (err) {
    $("#modalBody").innerHTML = `<div class="empty">Ошибка: ${escapeHtml(err.message)}</div>`;
  }
}

function renderProject(project) {
  const installed = installedIds().has(project.id);
  const kind = project.project_type === "resourcepack"
    ? "resourcepack"
    : project.project_type === "shader" ? "shader" : "mod";
  const label = CONTENT_LABELS[kind] || CONTENT_LABELS.mod;
  const gallery = (project.gallery || [])
    .map((shot, index) => `<img src="${shot.url}" data-index="${index}" alt="${escapeHtml(shot.title || "")}" title="${escapeHtml(shot.title || "")}">`)
    .join("");
  state.modalGallery = (project.gallery || []).map((shot) => shot.url);
  const versions = (project.versions || [])
    .slice(0, 6)
    .map((version) => `<div class="row-item">${escapeHtml(version.version || "")} · `
      + `${escapeHtml((version.game_versions || []).slice(-3).join(", "))} · `
      + `${escapeHtml((version.loaders || []).join(", "))}</div>`)
    .join("");
  const chips = [label.one, ...(project.categories || []).slice(0, 5)]
    .map((value) => `<span class="chip">${escapeHtml(value)}</span>`)
    .join("");
  const authors = (project.authors || [])
    .map((author) => `<a href="#" class="author-link" data-url="${escapeHtml(author.url)}" title="${escapeHtml(author.role || "")}">@${escapeHtml(author.username)}</a>`)
    .join(", ");

  $("#modalBody").innerHTML = `
    <div class="project-head">
      <img src="${project.icon_hd || project.icon || ""}" alt="" data-fallback="${project.icon || ""}" onerror="fallbackIcon(this)">
      <div>
        <div class="project-title">${escapeHtml(project.title)}</div>
        <div class="project-sub">${fmt(project.downloads)} загрузок${project.license ? " · " + escapeHtml(project.license) : ""}</div>
        ${authors.length ? `<div class="project-authors">Создатели: ${authors}</div>` : ""}
        <div class="chips">${chips}</div>
      </div>
    </div>
    ${gallery ? `<div class="gallery">${gallery}</div>` : `<div class="gallery-empty" style="margin-top:14px">Скриншотов нет</div>`}
    <div class="project-actions">
      <button class="btn primary" id="modalInstall" ${installed ? "disabled" : ""}>${installed ? "Установлено" : "Установить"}</button>
      <button class="btn ghost small" id="modalTranslate">Показать оригинал</button>
      <button class="btn ghost small" id="modalModrinth">Открыть на Modrinth</button>
    </div>
    <div class="project-body" id="projectBody"><span class="spinner"></span></div>
    <div class="versions">${versions}</div>`;

  $("#modalInstall").addEventListener("click", () => installProject(project.id, kind, $("#modalInstall")));
  $("#modalTranslate").addEventListener("click", toggleDescription);
  $("#modalModrinth").textContent = project.source === "curseforge" ? "Открыть на CurseForge" : "Открыть на Modrinth";
  if (project.source === "file") {
    $("#modalModrinth").style.display = "none";
    const fileInstall = $("#modalInstall");
    if (fileInstall) {
      fileInstall.textContent = "Установлено";
      fileInstall.disabled = true;
    }
  }
  $("#modalModrinth").addEventListener("click", () =>
    openUrl(project.page_url || `https://modrinth.com/${kind === "mod" ? "mod" : kind}/${project.slug}`));
  $$("#modalBody .author-link").forEach((link) =>
    link.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      openUrl(link.dataset.url);
    }));
}

async function translateDescription(project) {
  const box = $("#projectBody");
  if (!box) return;
  const original = stripMarkdown(project.body || "");
  state.modalOriginal = original;
  if (!original) {
    box.className = "body-empty";
    box.textContent = "Описание не заполнено.";
    const button = $("#modalTranslate");
    if (button) button.style.display = "none";
    return;
  }
  box.textContent = original;
  try {
    const data = await api("/api/translate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text: original, target: "ru" }),
    });
    if (data.text && state.modalOriginal === original) {
      state.modalTranslated = data.text;
      box.textContent = data.text;
    }
  } catch (err) {
    const button = $("#modalTranslate");
    if (button) button.textContent = "Перевод недоступен";
  }
}

function toggleDescription() {
  const box = $("#projectBody");
  const button = $("#modalTranslate");
  if (!box || !state.modalOriginal) return;
  state.modalShowOriginal = !state.modalShowOriginal;
  box.textContent = state.modalShowOriginal ? state.modalOriginal : (state.modalTranslated || state.modalOriginal);
  if (button) button.textContent = state.modalShowOriginal ? "Перевод на русский" : "Показать оригинал";
}

/* ---------------------------------------------------------------- lightbox */

function applyLightboxTransform() {
  const image = $("#lbImage");
  if (!image) return;
  image.style.transform = `translate(${state.lbX}px, ${state.lbY}px) scale(${state.lbZoom})`;
}

function updateLightbox() {
  const images = state.lbImages || [];
  if (!images.length) return;
  const image = $("#lbImage");
  image.src = images[state.lbIndex];
  image.classList.toggle("zoomed", state.lbZoom > 1);
  applyLightboxTransform();
  $("#lbCounter").textContent = `${state.lbIndex + 1} / ${images.length}`;
  const multiple = images.length > 1;
  $("#lbPrev").style.visibility = multiple ? "visible" : "hidden";
  $("#lbNext").style.visibility = multiple ? "visible" : "hidden";
}

function openLightbox(images, index) {
  if (!images || !images.length) return;
  state.lbImages = images;
  state.lbIndex = Math.min(Math.max(0, index || 0), images.length - 1);
  state.lbZoom = 1;
  state.lbX = 0;
  state.lbY = 0;
  const box = $("#lightbox");
  if (!box) return;
  box.classList.add("on");
  updateLightbox();
}

function closeLightbox() {
  const box = $("#lightbox");
  if (box) box.classList.remove("on");
}

function stepLightbox(delta) {
  const images = state.lbImages || [];
  if (!images.length) return;
  state.lbIndex = (state.lbIndex + delta + images.length) % images.length;
  state.lbZoom = 1;
  state.lbX = 0;
  state.lbY = 0;
  updateLightbox();
}

function zoomLightbox(delta) {
  state.lbZoom = Math.min(4, Math.max(1, Math.round((state.lbZoom + delta) * 10) / 10));
  if (state.lbZoom === 1) {
    state.lbX = 0;
    state.lbY = 0;
  }
  updateLightbox();
}

/* ------------------------------------------------------------------- files */

function fmtBytes(size) {
  if (size >= 1024 * 1024) return (size / (1024 * 1024)).toFixed(1) + " МБ";
  if (size >= 1024) return (size / 1024).toFixed(1) + " КБ";
  return size + " Б";
}

async function loadFiles() {
  const select = $("#fileDest");
  if (!select) return;
  const dest = select.value;
  const box = $("#fileList");
  box.innerHTML = `<div class="empty"><span class="spinner"></span> Читаю папку…</div>`;
  try {
    const data = await api(`/api/files/list?dest=${encodeURIComponent(dest)}`);
    $("#fileDestHint").textContent = data.path || "";
    if (!data.files.length) {
      box.innerHTML = `<div class="empty">Папка пуста</div>`;
      return;
    }
    box.innerHTML = data.files.map((file) => `
      <div class="list-item">
        <div class="grow">
          <div class="name">${escapeHtml(file.name)}</div>
          <div class="meta">${fmtBytes(file.size)} · ${new Date(file.modified * 1000).toLocaleString("ru-RU")}</div>
        </div>
        <button class="btn small danger" data-del-file="${escapeHtml(file.name)}">Удалить</button>
      </div>`).join("");
  } catch (err) {
    box.innerHTML = `<div class="empty">Ошибка: ${escapeHtml(err.message)}</div>`;
  }
}

async function uploadFiles(fileList) {
  const files = Array.from(fileList || []);
  if (!files.length) return;
  const dest = $("#fileDest").value;
  let added = 0;
  const skipped = [];
  for (const file of files) {
    const send = (overwrite) => fetch(
      `/api/files/upload?dest=${encodeURIComponent(dest)}&name=${encodeURIComponent(file.name)}${overwrite ? "&overwrite=1" : ""}`,
      { method: "POST", headers: { "Content-Type": "application/octet-stream" }, body: file },
    );
    try {
      let response = await send(false);
      let data = await response.json().catch(() => ({}));
      if (!response.ok && /уже есть/i.test(data.error || "") && confirm(`Заменить «${file.name}»?`)) {
        response = await send(true);
        data = await response.json().catch(() => ({}));
      }
      if (!response.ok) {
        skipped.push(`${file.name}: ${data.error || "HTTP " + response.status}`);
        continue;
      }
      added += 1;
    } catch (err) {
      skipped.push(`${file.name}: ${err.message}`);
    }
  }
  if (added) toast(`Добавлено файлов: ${added}`);
  if (skipped.length) {
    toast(skipped[0] + (skipped.length > 1 ? ` (и ещё ${skipped.length - 1})` : ""), true);
  }
  loadFiles();
}

/* ------------------------------------------------------------------- skin */

function setSkinFile(file) {
  if (!file) return;
  if (file.size > 1024 * 1024) return toast("Файл скина больше 1 МБ", true);
  const reader = new FileReader();
  reader.onload = async () => {
    const dataUrl = reader.result;
    await loadSkinImage(dataUrl);
    state.pendingSkin = dataUrl;
    $("#skinHint").textContent = "Файл выбран. Нажмите «Применить локально» или «Загрузить на ely.by».";
  };
  reader.readAsDataURL(file);
}

async function uploadSkin(target) {
  if (!state.pendingSkin) return toast("Сначала выберите PNG-файл", true);
  const blob = await (await fetch(state.pendingSkin)).blob();
  const model = $("#skinModel").value;
  try {
    const result = await api(target, {
      method: "POST",
      headers: { "Content-Type": "image/png", "X-Skin-Model": model },
      body: blob,
    });
    toast(target === "/api/skin/ely" ? "Скин загружен на ely.by" : "Скин применён локально");
    state.pendingSkin = null;
    if (result.skin && result.skin.data_url) await loadSkinImage(result.skin.data_url);
    $("#skinHint").textContent = target === "/api/skin/ely"
      ? "Скин отправлен на ely.by и появится в игре после перезапуска."
      : "Локальный скин сохранён.";
  } catch (err) {
    toast(err.message, true);
  }
}

/* --------------------------------------------------------------- settings */

async function saveSettings() {
  const payload = {
    memory_mb: parseInt($("#setMemory").value, 10) || 4096,
    java_path: $("#setJava").value.trim(),
    theme: document.documentElement.dataset.theme || "dark",
    reduce_motion: $("#optReduceMotion").checked,
    auto_java: $("#optAutoJava").checked,
    auto_join: $("#optAutoJoin").checked,
    holiday_theme: $("#optHoliday") ? $("#optHoliday").checked : true,
    content_view: state.contentView,
    curseforge_key: $("#setCurseforgeKey") ? $("#setCurseforgeKey").value.trim() : "",
  };
  try {
    await api("/api/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    toast("Настройки сохранены");
    await refresh();
  } catch (err) {
    toast(err.message, true);
  }
}

async function openFolder(target) {
  try { await api("/api/open", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ target }) }); }
  catch (err) { toast(err.message, true); }
}

async function openUrl(url) {
  try {
    await api("/api/open-url", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url }),
    });
  } catch (err) {
    toast(err.message, true);
  }
}

function copyServer() {
  const server = (state.overview?.manifest?.server) || {};
  const text = `${server.host || "craftunity.mcsh.io"}:${server.port || 25565}`;
  navigator.clipboard.writeText(text).then(() => toast("IP скопирован: " + text), () => toast(text));
}

/* ------------------------------------------------------------------ events */

function bindEvents() {
  $$("#nav button").forEach((btn) => btn.addEventListener("click", () => switchView(btn.dataset.view)));
  $("#playBtn").addEventListener("click", async () => {
    try {
      await api("/api/launch", { method: "POST" });
      $("#logBox").classList.add("on");
      toast("Запускаю Minecraft…");
    } catch (err) { toast(err.message, true); }
  });
  $("#stopBtn").addEventListener("click", async () => {
    try { await api("/api/stop", { method: "POST" }); toast("Игра остановлена"); }
    catch (err) { toast(err.message, true); }
  });
  $("#logToggle").addEventListener("click", () => {
    $("#logBox").classList.toggle("on");
    refresh();
  });
  $("#checkBtn").addEventListener("click", async () => {
    try {
      await api("/api/repair", { method: "POST" });
      $("#logBox").classList.add("on");
      toast("Проверяю целостность файлов по хешам…");
    } catch (err) { toast(err.message, true); }
  });

  $("#modSearchBtn").addEventListener("click", () => searchMods($("#modQuery").value.trim()));
  $("#modQuery").addEventListener("keydown", (e) => { if (e.key === "Enter") searchMods($("#modQuery").value.trim()); });
  $("#modMore").addEventListener("click", () => searchMods(state.modQuery, true));
  $("#modSort").addEventListener("change", () => searchMods($("#modQuery").value.trim()));
  $$("#contentTabs button").forEach((btn) =>
    btn.addEventListener("click", () => switchContentKind(btn.dataset.kind)));
  $$("#contentViewModes button").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.contentView === state.contentView);
    btn.addEventListener("click", () => {
      state.contentView = btn.dataset.contentView || "tiles";
      $$("#contentViewModes button").forEach((other) => other.classList.toggle("active", other === btn));
      renderMods();
      renderInstalled();
      saveSettings();
    });
  });
  syncUploadButton(state.contentKind);
  $("#uploadPack").addEventListener("click", () => $("#packFile").click());
  $("#packFile").addEventListener("change", async (e) => {
    const file = e.target.files[0];
    e.target.value = "";
    if (!file) return;
    const isMod = state.contentKind === "mod";
    const suffix = isMod ? ".jar" : ".zip";
    if (!file.name.toLowerCase().endsWith(suffix)) {
      toast(isMod ? "Для модов нужен файл .jar" : "Для ресурспаков и шейдеров нужен .zip", true);
      return;
    }
    try {
      const response = await fetch(
        `/api/content/upload?kind=${encodeURIComponent(state.contentKind)}&name=${encodeURIComponent(file.name)}`,
        {
          method: "POST",
          headers: { "Content-Type": isMod ? "application/java-archive" : "application/zip" },
          body: file,
        },
      );
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
      toast("Файл добавлен: " + (data.item?.filename || file.name));
      loadInstalled();
    } catch (err) {
      toast(err.message, true);
    }
  });
  $("#modResults").addEventListener("click", async (e) => {
    const id = e.target.dataset.install;
    if (id) {
      const result = await installProject(id, state.contentKind, e.target);
      if (result?.mod?.needs_iris === false) {
        setTimeout(() => toast("Шейдер поставлен, но Iris не установился — поставьте мод Iris вручную", true), 800);
      }
      return;
    }
    const card = e.target.closest("[data-open]");
    if (card) openProject(card.dataset.open);
  });
  $("#installedMods").addEventListener("click", async (e) => {
    if (e.target.dataset.installDeps) {
      const projectId = e.target.dataset.installDeps;
      const button = e.target;
      button.disabled = true;
      const previous = button.textContent;
      button.textContent = "Ставлю…";
      try {
        const data = await api("/api/mods/install-dependencies", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ project_id: projectId }),
        });
        const count = data.count || 0;
        const errors = data.errors || [];
        if (count) {
          toast(`Доставлено зависимостей: ${count}`);
        } else if (errors.length) {
          toast(`Не удалось поставить: ${errors[0].error || "ошибка"}`, true);
        } else {
          toast("Недостающих зависимостей не найдено", true);
        }
        await loadInstalled();
      } catch (err) {
        toast(err.message, true);
        button.disabled = false;
        button.textContent = previous;
      }
      return;
    }
    if (e.target.dataset.toggleFile) {
      const enabled = e.target.dataset.enabled !== "1";
      try {
        e.target.disabled = true;
        await api("/api/content/toggle", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            kind: e.target.dataset.kind,
            filename: e.target.dataset.filename,
            enabled,
          }),
        });
        toast(enabled ? "Включено" : "Выключено");
        loadInstalled();
      } catch (err) { toast(err.message, true); e.target.disabled = false; }
      return;
    }
    if (e.target.dataset.toggle !== undefined && !e.target.dataset.toggleFile) {
      const enabled = e.target.dataset.enabled !== "1";
      try {
        e.target.disabled = true;
        await api("/api/content/toggle", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ project_id: e.target.dataset.toggle, enabled }),
        });
        toast(enabled ? "Включено" : "Выключено");
        loadInstalled();
      } catch (err) { toast(err.message, true); e.target.disabled = false; }
      return;
    }
    if (e.target.dataset.removeFile) {
      if (!confirm("Удалить файл?")) return;
      try {
        await api("/api/content/remove-file", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ kind: e.target.dataset.kind, filename: e.target.dataset.filename }),
        });
        toast("Файл удалён");
        loadInstalled();
      } catch (err) { toast(err.message, true); }
      return;
    }
    if (e.target.dataset.remove) {
      const projectId = e.target.dataset.remove;
      const mod = (state.installed || []).find((item) => item.project_id === projectId);
      const name = (mod && (mod.title || mod.slug)) || projectId;
      if (!confirm(`Удалить «${name}»?`)) return;
      try {
        await api("/api/mods/remove", {
          method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ project_id: projectId }),
        });
        toast("Удалено");
        loadInstalled();
      } catch (err) {
        await confirmDependencyRemoval(projectId, name);
      }
      return;
    }
    const card = e.target.closest("[data-open]");
    if (card) openProject(card.dataset.open);
  });
  $$("#installedTabs button").forEach((btn) =>
    btn.addEventListener("click", () => {
      state.installedFilter = btn.dataset.filter || "all";
      $$("#installedTabs button").forEach((other) => other.classList.toggle("active", other === btn));
      renderInstalled();
    }));

  $("#modalClose").addEventListener("click", closeProject);
  $("#projectModal").addEventListener("click", (e) => { if (e.target.id === "projectModal") closeProject(); });
  $("#modalBody").addEventListener("click", (e) => {
    const image = e.target.closest(".gallery img");
    if (!image) return;
    openLightbox(state.modalGallery || [], Number(image.dataset.index || 0));
  });
  document.addEventListener("keydown", (e) => {
    const lightbox = $("#lightbox");
    if (lightbox && lightbox.classList.contains("on")) {
      if (e.key === "Escape") closeLightbox();
      else if (e.key === "ArrowRight") stepLightbox(1);
      else if (e.key === "ArrowLeft") stepLightbox(-1);
      return;
    }
    if (e.key === "Escape") closeProject();
  });
  $("#lbClose").addEventListener("click", closeLightbox);
  $("#lbPrev").addEventListener("click", () => stepLightbox(-1));
  $("#lbNext").addEventListener("click", () => stepLightbox(1));
  $("#lbZoomIn").addEventListener("click", () => zoomLightbox(0.5));
  $("#lbZoomOut").addEventListener("click", () => zoomLightbox(-0.5));
  $("#lightbox").addEventListener("click", (e) => { if (e.target.id === "lightbox") closeLightbox(); });
  $("#lbImage").addEventListener("dblclick", () => zoomLightbox(state.lbZoom > 1 ? 1 - state.lbZoom : 1));
  $("#lbImage").addEventListener("wheel", (e) => {
    if (!state.lbImages.length) return;
    e.preventDefault();
    zoomLightbox(e.deltaY < 0 ? 0.25 : -0.25);
  }, { passive: false });
  let lbDrag = null;
  $("#lbImage").addEventListener("pointerdown", (e) => {
    if (state.lbZoom <= 1) return;
    lbDrag = { x: e.clientX - state.lbX, y: e.clientY - state.lbY };
    e.target.setPointerCapture(e.pointerId);
  });
  $("#lbImage").addEventListener("pointermove", (e) => {
    if (!lbDrag) return;
    state.lbX = e.clientX - lbDrag.x;
    state.lbY = e.clientY - lbDrag.y;
    applyLightboxTransform();
  });
  const endDrag = (e) => {
    if (!lbDrag) return;
    lbDrag = null;
    try { e.target.releasePointerCapture(e.pointerId); } catch (err) { /* ignore */ }
  };
  $("#lbImage").addEventListener("pointerup", endDrag);
  $("#lbImage").addEventListener("pointercancel", endDrag);

  $("#skinDrop").addEventListener("click", () => $("#skinFile").click());
  $("#skinFile").addEventListener("change", (e) => setSkinFile(e.target.files[0]));
  ["dragenter", "dragover"].forEach((evt) => $("#skinDrop").addEventListener(evt, (e) => {
    e.preventDefault(); $("#skinDrop").classList.add("over");
  }));
  ["dragleave", "drop"].forEach((evt) => $("#skinDrop").addEventListener(evt, (e) => {
    e.preventDefault(); $("#skinDrop").classList.remove("over");
  }));
  $("#skinDrop").addEventListener("drop", (e) => setSkinFile(e.dataTransfer.files[0]));
  $("#skinApply").addEventListener("click", () => uploadSkin("/api/skin/upload"));
  $("#skinUploadEly").addEventListener("click", () => {
    openUrl("https://account.ely.by/skins");
    toast("Скин для аккаунта ely.by меняется на сайте");
  });
  $("#skinModel").addEventListener("change", () => toast("Модель скина: " + $("#skinModel").value));

  $("#fileDest").addEventListener("change", loadFiles);
  $("#fileDrop").addEventListener("click", () => $("#fileInput").click());
  ["dragenter", "dragover"].forEach((evt) => $("#fileDrop").addEventListener(evt, (e) => {
    e.preventDefault(); $("#fileDrop").classList.add("over");
  }));
  ["dragleave", "drop"].forEach((evt) => $("#fileDrop").addEventListener(evt, (e) => {
    e.preventDefault(); $("#fileDrop").classList.remove("over");
  }));
  $("#fileDrop").addEventListener("drop", (e) => uploadFiles(e.dataTransfer.files));
  $("#fileInput").addEventListener("change", (e) => {
    uploadFiles(e.target.files);
    e.target.value = "";
  });
  $("#fileList").addEventListener("click", async (e) => {
    const name = e.target.dataset.delFile;
    if (!name) return;
    if (!confirm(`Удалить «${name}»?`)) return;
    try {
      await api("/api/files/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ dest: $("#fileDest").value, name }),
      });
      toast("Файл удалён");
      loadFiles();
    } catch (err) { toast(err.message, true); }
  });

  $("#offlineBtn").addEventListener("click", async () => {
    try {
      await api("/api/account/offline", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: $("#offlineName").value.trim() }),
      });
      toast("Оффлайн-аккаунт выбран");
      switchView("play");
      refresh();
    } catch (err) { toast(err.message, true); }
  });
  $("#elyBtn").addEventListener("click", async () => {
    const btn = $("#elyBtn");
    btn.disabled = true;
    $("#elyHint").textContent = "Связываюсь с ely.by…";
    try {
      await api("/api/account/ely", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: $("#elyLogin").value.trim(), password: $("#elyPassword").value }),
      });
      $("#elyHint").textContent = "Вход выполнен.";
      $("#elyPassword").value = "";
      toast("Вход через ely.by выполнен");
      switchView("play");
      refresh();
    } catch (err) {
      $("#elyHint").textContent = err.message;
      toast(err.message, true);
    } finally {
      btn.disabled = false;
    }
  });
  $("#logoutBtn").addEventListener("click", async () => {
    await api("/api/account/logout", { method: "POST" });
    toast("Вы вышли из аккаунта");
    refresh();
  });
  $("#elyRegister").addEventListener("click", () => openUrl("https://account.ely.by/registration"));

  $("#saveSettings").addEventListener("click", saveSettings);
  const cfKeyInput = $("#setCurseforgeKey");
  const cfSaveKey = async () => {
    const key = (cfKeyInput ? cfKeyInput.value : "").trim();
    if (!key) {
      toast("Вставьте ключ со страницы console.curseforge.com", true);
      return;
    }
    try {
      const result = await api("/api/curseforge/check", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ key }),
      });
      if (result.ok) {
        toast("Ключ принят — CurseForge включён");
        await refresh();
      } else {
        toast(result.error || "CurseForge не принял ключ", true);
      }
    } catch (err) { toast(err.message, true); }
  };
  if ($("#cfGetKey")) $("#cfGetKey").addEventListener("click", () => openUrl("https://console.curseforge.com/?#/api-keys"));
  if ($("#cfSaveKey")) $("#cfSaveKey").addEventListener("click", cfSaveKey);
  if (cfKeyInput) cfKeyInput.addEventListener("change", cfSaveKey);
  $("#themeToggle").addEventListener("click", () => {
    const current = document.documentElement.dataset.theme || "dark";
    const index = THEMES.findIndex((item) => item.id === current);
    setTheme(THEMES[(index + 1) % THEMES.length].id);
  });
  $$("#themeTiles button").forEach((btn) =>
    btn.addEventListener("click", () => setTheme(btn.dataset.theme)));
  $("#optReduceMotion").addEventListener("change", (e) => {
    document.documentElement.classList.toggle("no-motion", e.target.checked);
    saveSettings();
  });
  $("#optAutoJava").addEventListener("change", saveSettings);
  $("#optAutoJoin").addEventListener("change", saveSettings);
  if ($("#optHoliday")) $("#optHoliday").addEventListener("change", saveSettings);
  $("#refreshManifest").addEventListener("click", async () => {
    try {
      const result = await api("/api/manifest/refresh", { method: "POST" });
      toast(result.ok ? "Манифест обновлён и подпись верна" : result.error || "Манифест недоступен", !result.ok);
      refresh();
    } catch (err) { toast(err.message, true); }
  });
}

async function boot() {
  bindEvents();
  await refresh();
  await loadInstalled();
  state.polling = setInterval(refresh, 1200);
  setInterval(refreshGreeting, 60000);
}

boot();
