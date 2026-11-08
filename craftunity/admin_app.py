from __future__ import annotations

import base64
import hashlib
import json
import os
import queue
import threading
import time
import tkinter as tk
import traceback
import urllib.request
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from . import config, manifest
from . import catalog
from . import dropbox as dropbox_store
from . import mods as modrinth_api
from . import packstore
from . import palette as palette_mod
from . import skins
from .admin_server import (
    ManifestServer,
    clients_for_admin,
    delete_clients,
    publish_dir,
    read_heartbeat_config,
    save_heartbeat_config,
)
from .discovery import Beacon, lan_ip
from .util import LauncherError, ssl_context

THEME = palette_mod.launcher_theme()
PALETTE = palette_mod.palette(THEME)

BG = PALETTE["bg"]
BG_ALT = PALETTE["bg_alt"]
CARD = PALETTE["card"]
FG = PALETTE["fg"]
MUTED = PALETTE["muted"]
ACCENT = PALETTE["accent"]
BORDER = PALETTE["border"]
ACCENT_FG = PALETTE["accent_fg"]
ACCENT_HOVER = PALETTE["accent_hover"]
ACCENT_DISABLED_BG = PALETTE["accent_disabled_bg"]
ACCENT_DISABLED_FG = PALETTE["accent_disabled_fg"]
CARD_HOVER = PALETTE["card_hover"]
SEL_BG = PALETTE["sel_bg"]


class ScrollTab(ttk.Frame):
    """Вкладка с вертикальной прокруткой: содержимое кладётся во внутренний фрейм."""

    def __init__(self, master, padding: int = 16) -> None:
        super().__init__(master)
        self.canvas = tk.Canvas(self, bg=BG, highlightthickness=0, bd=0)
        self.scrollbar = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        self.scrollbar.pack(side="right", fill="y")
        self.inner = ttk.Frame(self.canvas, padding=padding)
        self._window = self.canvas.create_window((0, 0), window=self.inner, anchor="nw")
        self.inner.bind("<Configure>", self._sync_region)
        self.canvas.bind("<Configure>", self._sync_width)
        self.canvas.bind("<Enter>", self._bind_wheel)
        self.canvas.bind("<Leave>", self._unbind_wheel)

    def _sync_region(self, _event=None) -> None:
        self.canvas.configure(scrollregion=self.canvas.bbox("all"))

    def _sync_width(self, event) -> None:
        self.canvas.itemconfigure(self._window, width=event.width)

    def _bind_wheel(self, _event=None) -> None:
        self.canvas.bind_all("<MouseWheel>", self._on_wheel)

    def _unbind_wheel(self, _event=None) -> None:
        self.canvas.unbind_all("<MouseWheel>")

    def _on_wheel(self, event) -> None:
        self.canvas.yview_scroll(int(-event.delta / 120), "units")


class AvatarLoader:
    """Подгружает скины клиентов и рисует голову для таблицы."""

    def __init__(self, root: tk.Tk, on_ready) -> None:
        self.root = root
        self.on_ready = on_ready
        self.images: dict[str, tk.PhotoImage | None] = {}
        self.pending: set[str] = set()
        self.queue: queue.Queue = queue.Queue()
        self.root.after(100, self._drain)

    def get(self, username: str):
        if not username:
            return None
        if username in self.images:
            return self.images[username]
        placeholder = self._fallback(username)
        self.images[username] = placeholder
        if username not in self.pending:
            self.pending.add(username)
            threading.Thread(target=self._load, args=(username,), daemon=True).start()
        return placeholder

    def _load(self, username: str) -> None:
        raw = skins.fetch_skin_bytes(username)
        self.queue.put((username, raw))

    def _drain(self) -> None:
        changed = False
        while True:
            try:
                username, raw = self.queue.get_nowait()
            except queue.Empty:
                break
            self._finish(username, raw)
            changed = True
        if changed and self.on_ready:
            self.on_ready()
        try:
            self.root.after(100, self._drain)
        except tk.TclError:
            pass

    def _finish(self, username: str, raw) -> None:
        try:
            self.images[username] = self._build(raw) if raw else self._fallback(username)
        except Exception:
            try:
                self.images[username] = self._fallback(username)
            except Exception:
                self.images[username] = None
        finally:
            self.pending.discard(username)

    @staticmethod
    def _build(raw: bytes):
        source = tk.PhotoImage(data=base64.b64encode(raw))
        head = tk.PhotoImage(width=8, height=8)
        head.tk.call(head, "copy", source, "-from", 8, 8, 16, 16)
        try:
            head.tk.call(head, "copy", source, "-from", 40, 8, 48, 16, "-compositingrule", "overlay")
        except tk.TclError:
            pass
        return head.zoom(4)

    @staticmethod
    def _fallback(username: str):
        digest = hashlib.sha1(username.encode("utf-8")).digest()
        color = "#%02x%02x%02x" % (60 + digest[0] % 120, 60 + digest[1] % 120, 60 + digest[2] % 120)
        image = tk.PhotoImage(width=32, height=32)
        image.put(color, to=(0, 0, 32, 32))
        image.put("#f5f7ff", to=(6, 6, 26, 26))
        image.put(color, to=(10, 10, 22, 22))
        return image


class AdminApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.server: ManifestServer | None = None
        self.beacon: Beacon | None = None
        self.mods: list[dict] = []
        self.result_buttons: dict[str, ttk.Button] = {}
        self.manifest_data: dict = {}
        self._clients_timer = None
        self.ui_queue: queue.Queue = queue.Queue()
        self.search_query = ""
        self.search_offset = 0
        self.search_total = 0

        root.title("Craftunity Admin")
        screen_w = root.winfo_screenwidth()
        screen_h = root.winfo_screenheight()
        width = min(980, max(720, screen_w - 120))
        height = min(700, max(520, screen_h - 160))
        root.geometry(f"{width}x{height}")
        root.minsize(680, 460)
        root.configure(bg=BG)
        icon = config.resource_path("icon_admin.ico")
        if not icon.exists():
            icon = config.resource_path("icon.ico")
        if icon.exists():
            try:
                root.iconbitmap(str(icon))
            except tk.TclError:
                pass

        self._setup_style()
        self._build_layout()
        self.avatars = AvatarLoader(self.root, self._on_avatars_ready)
        self.load_manifest()
        self.refresh_clients()
        self.root.after(100, self._drain_ui)
        root.protocol("WM_DELETE_WINDOW", self.on_close)

    # ------------------------------------------------------------------ style

    def _setup_style(self) -> None:
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(".", background=BG, foreground=FG, fieldbackground=BG_ALT, bordercolor=BORDER)
        style.configure("TFrame", background=BG)
        style.configure("Card.TFrame", background=BG_ALT)
        style.configure("TLabel", background=BG, foreground=FG, font=("Segoe UI", 10))
        style.configure("Muted.TLabel", background=BG, foreground=MUTED, font=("Segoe UI", 9))
        style.configure("Card.TLabel", background=BG_ALT, foreground=FG, font=("Segoe UI", 10))
        style.configure("CardMuted.TLabel", background=BG_ALT, foreground=MUTED, font=("Segoe UI", 9))
        style.configure("H1.TLabel", background=BG, foreground=FG, font=("Segoe UI Semibold", 16))
        style.configure("TNotebook", background=BG, borderwidth=0)
        style.configure("TNotebook.Tab", background=BG_ALT, foreground=MUTED, padding=(18, 9), borderwidth=0)
        style.map("TNotebook.Tab", background=[("selected", CARD)], foreground=[("selected", ACCENT)])
        style.configure("TButton", background=CARD, foreground=FG, borderwidth=0, padding=(20, 11), font=("Segoe UI", 11), focusthickness=0)
        style.map("TButton", background=[("active", CARD_HOVER), ("disabled", BG_ALT)], foreground=[("disabled", MUTED)])
        style.configure("Accent.TButton", background=ACCENT, foreground=ACCENT_FG, font=("Segoe UI Semibold", 11), padding=(22, 12))
        style.map("Accent.TButton", background=[("active", ACCENT_HOVER), ("disabled", ACCENT_DISABLED_BG)], foreground=[("disabled", ACCENT_DISABLED_FG)])
        style.configure("TEntry", fieldbackground=BG_ALT, foreground=FG, insertcolor=FG, borderwidth=0, padding=8)
        style.configure("TCombobox", fieldbackground=BG_ALT, background=BG_ALT, foreground=FG, padding=6)
        style.map("TCombobox", fieldbackground=[("readonly", BG_ALT)], foreground=[("readonly", FG)])
        style.configure("Treeview", background=BG_ALT, fieldbackground=BG_ALT, foreground=FG, borderwidth=0, rowheight=40)
        style.configure("Treeview.Heading", background=CARD, foreground=MUTED, relief="flat", font=("Segoe UI", 9))
        style.map("Treeview", background=[("selected", SEL_BG)])
        style.configure("TScrollbar", background=BG_ALT, troughcolor=BG, borderwidth=0, arrowcolor=MUTED)

    # ----------------------------------------------------------------- layout

    def _build_layout(self) -> None:
        header = ttk.Frame(self.root)
        header.pack(fill="x", padx=22, pady=(18, 6))
        ttk.Label(header, text="Craftunity Admin", style="H1.TLabel").pack(side="left")
        ttk.Label(
            header,
            text="одна версия клиента для всех лаунчеров",
            style="Muted.TLabel",
        ).pack(side="left", padx=14)

        self.notebook = ttk.Notebook(self.root)
        self.notebook.pack(fill="both", expand=True, padx=18, pady=(6, 0))

        self.page_build = ScrollTab(self.notebook)
        self.page_mods = ScrollTab(self.notebook)
        self.page_clients = ScrollTab(self.notebook)
        self.page_publish = ScrollTab(self.notebook)
        self.notebook.add(self.page_build, text="Сборка")
        self.notebook.add(self.page_mods, text="Модпак")
        self.notebook.add(self.page_clients, text="Клиенты")
        self.notebook.add(self.page_publish, text="Публикация")

        self.tab_build = self.page_build.inner
        self.tab_mods = self.page_mods.inner
        self.tab_clients = self.page_clients.inner
        self.tab_publish = self.page_publish.inner

        self._build_tab_build()
        self._build_tab_mods()
        self._build_tab_clients()
        self._build_tab_publish()

        status = ttk.Frame(self.root, padding=(22, 8))
        status.pack(fill="x")
        self.status_var = tk.StringVar(value="Готово")
        ttk.Label(status, textvariable=self.status_var, style="Muted.TLabel").pack(side="left")
        ttk.Label(
            status,
            text="v" + config.LAUNCHER_VERSION + " · тема: " + palette_mod.theme_name(THEME),
            style="Muted.TLabel",
        ).pack(side="right")

    def _clipboard(self, entry: ttk.Entry) -> ttk.Entry:
        """Вставка из буфера: Ctrl+V, Shift+Insert и меню по правой кнопке."""
        menu = tk.Menu(entry, tearoff=0)
        menu.add_command(label="Вставить", command=lambda: entry.event_generate("<<Paste>>"))
        menu.add_command(label="Копировать", command=lambda: entry.event_generate("<<Copy>>"))
        menu.add_command(label="Вырезать", command=lambda: entry.event_generate("<<Cut>>"))
        menu.add_separator()
        menu.add_command(label="Выделить всё", command=lambda: self._select_all(entry))

        def popup(event):
            entry.focus_set()
            try:
                menu.tk_popup(event.x_root, event.y_root)
            finally:
                menu.grab_release()
            return "break"

        entry.bind("<Button-3>", popup)
        entry.bind("<Shift-Insert>", lambda _event: (entry.event_generate("<<Paste>>"), "break")[1])
        entry.bind("<Control-v>", lambda _event: (entry.event_generate("<<Paste>>"), "break")[1])
        entry.bind("<Control-V>", lambda _event: (entry.event_generate("<<Paste>>"), "break")[1])
        entry.bind("<Control-a>", lambda _event: (self._select_all(entry), "break")[1])
        entry.bind("<Control-A>", lambda _event: (self._select_all(entry), "break")[1])
        return entry

    @staticmethod
    def _select_all(entry: ttk.Entry) -> None:
        entry.selection_range(0, "end")
        entry.icursor("end")

    def _field(self, parent: ttk.Frame, row: int, column: int, label: str, variable: tk.Variable, width: int = 26) -> ttk.Entry:
        ttk.Label(parent, text=label, style="Muted.TLabel").grid(row=row * 2, column=column, sticky="w", padx=(0, 12), pady=(0, 2))
        entry = self._clipboard(ttk.Entry(parent, textvariable=variable, width=width))
        entry.grid(row=row * 2 + 1, column=column, sticky="ew", padx=(0, 12), pady=(0, 10))
        return entry

    def _build_tab_build(self) -> None:
        card = ttk.Frame(self.tab_build, style="Card.TFrame", padding=18)
        card.pack(fill="both", expand=True)
        card.columnconfigure(0, weight=1)
        card.columnconfigure(1, weight=1)

        self.var_version = tk.StringVar()
        self.var_game = tk.StringVar()
        self.var_loader = tk.StringVar(value="fabric")
        self.var_loader_version = tk.StringVar()
        self.var_host = tk.StringVar()
        self.var_port = tk.StringVar()
        self.var_message = tk.StringVar()
        self.var_jvm = tk.StringVar()
        self.var_launcher_version = tk.StringVar()
        self.var_launcher_notes = tk.StringVar()
        self.var_launcher_mandatory = tk.BooleanVar(value=False)

        ttk.Label(card, text="Параметры сборки", style="Card.TLabel", font=("Segoe UI Semibold", 12)).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 12)
        )
        self._field(card, 1, 0, "Версия сборки", self.var_version)
        self._field(card, 1, 1, "Версия Minecraft", self.var_game)

        ttk.Label(card, text="Загрузчик", style="CardMuted.TLabel").grid(row=4, column=0, sticky="w", pady=(0, 2))
        loader_label = ttk.Label(card, text="Fabric — всегда", style="Card.TLabel")
        loader_label.grid(row=5, column=0, sticky="w", padx=(0, 12), pady=(0, 10))
        self._field(card, 3, 1, "Версия загрузчика (пусто = авто)", self.var_loader_version)

        self._field(card, 4, 0, "Хост сервера", self.var_host)
        self._field(card, 4, 1, "Порт", self.var_port)

        ttk.Label(card, text="JVM-аргументы", style="CardMuted.TLabel").grid(row=10, column=0, columnspan=2, sticky="w", pady=(0, 2))
        self.jvm_text = tk.Text(card, height=3, bg=BG, fg=FG, insertbackground=FG, relief="flat", padx=10, pady=8)
        self.jvm_text.grid(row=11, column=0, columnspan=2, sticky="ew", pady=(0, 10))

        ttk.Label(card, text="Сообщение игрокам", style="CardMuted.TLabel").grid(row=12, column=0, columnspan=2, sticky="w", pady=(0, 2))
        self.message_text = tk.Text(card, height=3, bg=BG, fg=FG, insertbackground=FG, relief="flat", padx=10, pady=8)
        self.message_text.grid(row=13, column=0, columnspan=2, sticky="ew", pady=(0, 14))

        ttk.Label(card, text="Обновление лаунчера", style="Card.TLabel", font=("Segoe UI Semibold", 12)).grid(
            row=14, column=0, columnspan=2, sticky="w", pady=(0, 6)
        )
        self._field(card, 8, 0, "Версия лаунчера (пусто = без обновления)", self.var_launcher_version)
        self._field(card, 8, 1, "Что нового", self.var_launcher_notes)
        ttk.Checkbutton(card, text="Обновить принудительно (старые версии не запустятся)", variable=self.var_launcher_mandatory).grid(
            row=18, column=0, columnspan=2, sticky="w", pady=(0, 10)
        )

        actions = ttk.Frame(card, style="Card.TFrame")
        actions.grid(row=20, column=0, columnspan=2, sticky="ew")
        ttk.Button(actions, text="Сохранить и опубликовать", style="Accent.TButton", command=self.save_manifest).pack(side="left")
        ttk.Button(actions, text="Перечитать", command=self.load_manifest).pack(side="left", padx=8)
        ttk.Button(actions, text="Открыть папку", command=self.open_publish_folder).pack(side="left")

        self.build_hint = ttk.Label(
            card,
            text="Изменения применяются у всех лаунчеров после запуска игры: они забирают подписанный манифест.",
            style="CardMuted.TLabel",
        )
        self.build_hint.grid(row=15, column=0, columnspan=2, sticky="w", pady=(14, 0))

    def _build_tab_mods(self) -> None:
        top = ttk.Frame(self.tab_mods)
        top.pack(fill="x")
        ttk.Label(top, text="Поиск модов в Modrinth", style="Muted.TLabel").pack(anchor="w")
        row = ttk.Frame(top)
        row.pack(fill="x", pady=(4, 8))
        self.var_search = tk.StringVar()
        entry = self._clipboard(ttk.Entry(row, textvariable=self.var_search, width=40))
        entry.pack(side="left")
        entry.bind("<Return>", lambda _event: self.search_modrinth())
        ttk.Button(row, text="Найти", command=self.search_modrinth).pack(side="left", padx=8)
        ttk.Button(row, text="Популярные", command=lambda: self.search_modrinth("")).pack(side="left")
        self.search_state = ttk.Label(row, text="", style="Muted.TLabel")
        self.search_state.pack(side="left", padx=12)

        body = ttk.Panedwindow(self.tab_mods, orient="horizontal")
        body.pack(fill="both", expand=True)

        left = ttk.Frame(body)
        self.more_btn = None
        self.results_canvas = tk.Canvas(left, bg=BG, highlightthickness=0, bd=0)
        scroll = ttk.Scrollbar(left, orient="vertical", command=self.results_canvas.yview)
        self.results_canvas.configure(yscrollcommand=scroll.set)
        self.results_canvas.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")
        self.results_frame = tk.Frame(self.results_canvas, bg=BG)
        self.results_window = self.results_canvas.create_window((0, 0), window=self.results_frame, anchor="nw")
        self.results_frame.bind(
            "<Configure>",
            lambda _event: self.results_canvas.configure(scrollregion=self.results_canvas.bbox("all")),
        )
        self.results_canvas.bind("<Configure>", self._resize_results)
        self.results_canvas.bind("<MouseWheel>", self._on_mousewheel)

        right = ttk.Frame(body)
        ttk.Label(right, text="Модпак сервера", style="Card.TLabel", font=("Segoe UI Semibold", 12)).pack(anchor="w", padx=4)
        mods_wrap = ttk.Frame(right)
        mods_wrap.pack(fill="both", expand=True, padx=4, pady=(6, 8))
        columns = ("project", "title")
        self.mods_tree = ttk.Treeview(mods_wrap, columns=columns, show="headings", height=14)
        self.mods_tree.heading("project", text="PROJECT ID / SLUG")
        self.mods_tree.heading("title", text="НАЗВАНИЕ")
        self.mods_tree.column("project", width=180, anchor="w")
        self.mods_tree.column("title", width=220, anchor="w")
        mods_scroll = ttk.Scrollbar(mods_wrap, orient="vertical", command=self.mods_tree.yview)
        self.mods_tree.configure(yscrollcommand=mods_scroll.set)
        self.mods_tree.pack(side="left", fill="both", expand=True)
        mods_scroll.pack(side="right", fill="y")
        ttk.Button(right, text="Убрать выбранное", command=self.remove_mod).pack(anchor="w", padx=4)
        ttk.Button(right, text="Добавить .jar файлом", command=self.add_file_mod).pack(anchor="w", padx=4, pady=(6, 0))

        body.add(left, weight=3)
        body.add(right, weight=2)

        ttk.Label(
            self.tab_mods,
            text="Нажмите «Добавить» на карточке — мод попадёт в модпак и будет ставиться при запуске игры.",
            style="Muted.TLabel",
        ).pack(anchor="w", pady=(8, 0))

        self.root.after(600, lambda: self.search_modrinth(""))

    def _drain_ui(self) -> None:
        while True:
            try:
                callback = self.ui_queue.get_nowait()
            except queue.Empty:
                break
            try:
                callback()
            except Exception:
                pass
        try:
            self.root.after(100, self._drain_ui)
        except tk.TclError:
            pass

    def _on_mousewheel(self, event) -> None:
        step = -2 if event.delta > 0 else 2
        self.results_canvas.yview_scroll(step, "units")

    def _bind_wheel(self, widget) -> None:
        widget.bind("<MouseWheel>", self._on_mousewheel)
        for child in widget.winfo_children():
            self._bind_wheel(child)

    def _resize_results(self, event) -> None:
        self.results_canvas.itemconfigure(self.results_window, width=event.width)

    def search_modrinth(self, query: str | None = None, append: bool = False) -> None:
        if query is None:
            query = self.var_search.get().strip()
        if not append:
            self.search_query = query
            self.search_offset = 0
        game = ((self.manifest_data.get("game") or {}).get("version")) or "1.20.1"
        offset = self.search_offset
        self.search_state.configure(text="Ищу в Modrinth и CurseForge…")

        def work() -> None:
            try:
                data = catalog.search_all(
                    self.search_query,
                    game,
                    "fabric",
                    limit=24,
                    offset=offset,
                    sort="relevance" if self.search_query else "downloads",
                    kind="mod",
                )
            except Exception as exc:  # noqa: BLE001
                self.ui_queue.put(lambda err=exc: self.search_state.configure(text=f"Ошибка: {err}"))
                return
            hits = data.get("hits") or []
            total = int(data.get("total_hits") or 0)
            warning = str(data.get("warning") or "")
            cf = data.get("curseforge") or {}
            suffix = ""
            if cf.get("enabled"):
                suffix = f" · CurseForge: {cf.get('found', 0)}, дублей скрыто: {cf.get('duplicates', 0)}"
            elif warning:
                suffix = ""
            self.ui_queue.put(lambda: self.search_state.configure(text=f"Найдено: {total}{suffix}"))
            self.ui_queue.put(lambda: self._render_results(hits, total, append))

        threading.Thread(target=work, daemon=True).start()

    def load_more(self) -> None:
        self.search_modrinth(append=True)

    def _render_results(self, hits: list[dict], total: int, append: bool = False) -> None:
        if not append:
            for child in self.results_frame.winfo_children():
                child.destroy()
            self.more_btn = None
            self.result_buttons.clear()
        elif self.more_btn is not None and self.more_btn.winfo_exists():
            self.more_btn.destroy()
            self.more_btn = None
        start = len(self.results_frame.winfo_children())
        for index, hit in enumerate(hits):
            position = start + index
            card = tk.Frame(self.results_frame, bg=CARD, highlightthickness=1, highlightbackground=BORDER)
            card.grid(row=position // 3, column=position % 3, padx=6, pady=6, sticky="nsew")
            icon_box = tk.Frame(card, bg=CARD, width=64, height=64)
            icon_box.pack_propagate(False)
            icon_box.pack(pady=(10, 6))
            icon = tk.Label(icon_box, text="…", bg=CARD, fg=MUTED)
            icon.pack(fill="both", expand=True)
            tk.Label(
                card,
                text=hit.get("title") or "",
                bg=CARD,
                fg=FG,
                wraplength=170,
                justify="center",
                font=("Segoe UI", 9, "bold"),
            ).pack(padx=8)
            tk.Label(
                card,
                text=f"{hit.get('downloads', 0):,}".replace(",", " "),
                bg=CARD,
                fg=MUTED,
                font=("Segoe UI", 8),
            ).pack(pady=(2, 2))
            tk.Label(
                card,
                text="CurseForge" if hit.get("source") == "curseforge" else "Modrinth",
                bg=CARD,
                fg=ACCENT if hit.get("source") == "curseforge" else MUTED,
                font=("Segoe UI", 7),
            ).pack(pady=(0, 4))
            project_id = hit.get("id") or hit.get("slug") or ""
            added = self._in_pack(project_id)
            button = ttk.Button(
                card,
                text="В модпаке" if added else "Добавить",
                command=lambda h=hit: self.add_from_hit(h),
            )
            if added:
                button.state(["disabled"])
            button.pack(pady=(0, 10))
            if project_id:
                self.result_buttons[project_id] = button
            self._load_icon(hit.get("icon"), icon)
        for column in range(3):
            self.results_frame.grid_columnconfigure(column, weight=1)
        self.search_offset = start + len(hits)
        self.search_total = total
        if not self.search_offset:
            self.search_state.configure(text="Ничего не найдено")
        else:
            self.search_state.configure(text=f"Показано {self.search_offset} из {total}")
        if hits and self.search_offset < total:
            self.more_btn = ttk.Button(self.results_frame, text="Больше модов", command=self.load_more)
            row = (self.search_offset + 2) // 3
            self.more_btn.grid(row=row, column=0, columnspan=3, sticky="ew", padx=6, pady=(6, 14))
        self._bind_wheel(self.results_frame)
        self.results_frame.update_idletasks()
        self.results_canvas.configure(
            scrollregion=(0, 0, max(self.results_canvas.winfo_width(), 1), self.results_frame.winfo_reqheight())
        )

    def _load_icon(self, url: str | None, label: tk.Label) -> None:
        if not url:
            return

        def work() -> None:
            try:
                request = urllib.request.Request(url, headers={"User-Agent": "CraftunityAdmin/1.0"})
                with urllib.request.urlopen(request, timeout=8, context=ssl_context()) as response:
                    raw = response.read()
            except Exception:
                return
            self.ui_queue.put(lambda: self._set_icon(label, raw))

        threading.Thread(target=work, daemon=True).start()

    def _set_icon(self, label: tk.Label, raw: bytes) -> None:
        try:
            try:
                import io

                from PIL import Image, ImageTk

                with Image.open(io.BytesIO(raw)) as source:
                    image = source.convert("RGBA").resize((64, 64), Image.LANCZOS)
                photo = ImageTk.PhotoImage(image)
            except ImportError:
                photo = tk.PhotoImage(data=base64.b64encode(raw))
                factor = max(1, photo.width() // 48)
                photo = photo.subsample(factor)
            label.configure(image=photo, text="")
            label.image = photo  # type: ignore[attr-defined]
        except Exception:
            pass

    def _in_pack(self, project_id: str) -> bool:
        if not project_id:
            return False
        return any((mod.get("project_id") or mod.get("slug")) == project_id for mod in self.mods)

    def _mark_added(self, project_id: str, added: bool) -> None:
        button = self.result_buttons.get(project_id)
        if button is None or not button.winfo_exists():
            return
        button.configure(text="В модпаке" if added else "Добавить")
        button.state(["disabled"] if added else ["!disabled"])

    def add_from_hit(self, hit: dict) -> None:
        project_id = hit.get("id") or hit.get("slug")
        if not project_id:
            return
        if self._in_pack(project_id):
            self._mark_added(project_id, True)
            self.set_status("Этот мод уже в модпаке")
            return
        self.mods.append(
            {
                "project_id": project_id,
                "slug": hit.get("slug") or project_id,
                "title": hit.get("title") or project_id,
                "icon": hit.get("icon") or "",
            }
        )
        self.render_mods()
        self._mark_added(project_id, True)
        self.set_status(f"Добавлено: {hit.get('title')} — не забудьте опубликовать")

    def _build_tab_clients(self) -> None:
        top = ttk.Frame(self.tab_clients)
        top.pack(fill="x", pady=(0, 10))
        ttk.Label(
            top,
            text="Клиенты, которые запускали игру и отправляли heartbeat на адрес из настроек лаунчера.",
            style="Muted.TLabel",
        ).pack(side="left")
        ttk.Button(top, text="Обновить", command=self.refresh_clients).pack(side="right")
        ttk.Button(top, text="Удалить выбранных", command=self.delete_selected_clients).pack(
            side="right", padx=(0, 8)
        )
        ttk.Button(top, text="Удалить офлайн", command=self.delete_offline_clients).pack(
            side="right", padx=(0, 8)
        )

        remote = ttk.Frame(self.tab_clients)
        remote.pack(fill="x", pady=(0, 10))
        ttk.Label(remote, text="Приёмник heartbeat", style="Muted.TLabel").pack(anchor="w")
        saved = read_heartbeat_config()
        self.var_hb_url = tk.StringVar(value=saved["url"])
        self.var_hb_token = tk.StringVar(value=saved["token"])
        self._clipboard(ttk.Entry(remote, textvariable=self.var_hb_url, width=52)).pack(side="left", padx=(0, 8), pady=(4, 0))
        self._clipboard(ttk.Entry(remote, textvariable=self.var_hb_token, width=22, show="*")).pack(side="left", padx=(0, 8), pady=(4, 0))
        ttk.Button(remote, text="Сохранить", command=self.save_heartbeat_receiver).pack(side="left", pady=(4, 0))

        clients_wrap = ttk.Frame(self.tab_clients)
        clients_wrap.pack(fill="both", expand=True)
        columns = ("user", "version", "loader", "seen", "online", "id")
        self.clients_tree = ttk.Treeview(clients_wrap, columns=columns, show="tree headings", height=15)
        self.clients_tree.heading("#0", text="")
        self.clients_tree.column("#0", width=52, minwidth=52, stretch=False, anchor="center")
        for key, title, width in (
            ("user", "ИГРОК", 180),
            ("version", "ВЕРСИЯ", 120),
            ("loader", "ЗАГРУЗЧИК", 120),
            ("seen", "ПОСЛЕДНЯЯ АКТИВНОСТЬ", 220),
            ("online", "В СЕТИ", 160),
            ("id", "ID КЛИЕНТА", 160),
        ):
            self.clients_tree.heading(key, text=title)
            self.clients_tree.column(key, width=width, anchor="w")
        clients_scroll = ttk.Scrollbar(clients_wrap, orient="vertical", command=self.clients_tree.yview)
        self.clients_tree.configure(yscrollcommand=clients_scroll.set)
        self.clients_tree.pack(side="left", fill="both", expand=True)
        clients_scroll.pack(side="right", fill="y")
        self.clients_tree.bind("<Double-1>", lambda _event: self._open_client_content())

        self.clients_hint = ttk.Label(
            self.tab_clients,
            text="Двойной клик по клиенту — список его модов, ресурспаков и шейдеров",
            style="Muted.TLabel",
        )
        self.clients_hint.pack(anchor="w", pady=(10, 0))

    def _build_tab_publish(self) -> None:
        card = ttk.Frame(self.tab_publish, style="Card.TFrame", padding=18)
        card.pack(fill="both", expand=True)

        ttk.Label(card, text="Раздача манифеста", style="Card.TLabel", font=("Segoe UI Semibold", 12)).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 6)
        )
        ttk.Label(
            card,
            text="Сервер нужен только для локальной сети и отладки: кнопка «Сохранить и опубликовать» "
                 "сама кладёт подписанный манифест в папку сервера Dropbox. "
                 "0.0.0.0 — слушать все интерфейсы; 127.0.0.1 — только этот компьютер.",
            style="CardMuted.TLabel",
            wraplength=780,
            justify="left",
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(0, 12))

        self.var_bind = tk.StringVar(value="0.0.0.0")
        self.var_srv_port = tk.StringVar(value="8765")
        self._field(card, 2, 0, "Адрес прослушивания", self.var_bind)
        self._field(card, 2, 1, "Порт", self.var_srv_port)

        actions = ttk.Frame(card, style="Card.TFrame")
        actions.grid(row=5, column=0, columnspan=2, sticky="ew", pady=(4, 10))
        self.server_btn = ttk.Button(actions, text="Запустить сервер", command=self.toggle_server)
        self.server_btn.pack(side="left")
        ttk.Button(actions, text="Открыть папку публикации", command=self.open_publish_folder).pack(side="left", padx=8)

        self.server_state = ttk.Label(card, text="Сервер остановлен", style="CardMuted.TLabel")
        self.server_state.grid(row=6, column=0, columnspan=2, sticky="w", pady=(0, 10))

        ttk.Label(card, text="URL манифеста для настроек лаунчера", style="CardMuted.TLabel").grid(row=7, column=0, sticky="w")
        self.url_var = tk.StringVar(value="—")
        url_entry = ttk.Entry(card, textvariable=self.url_var, state="readonly")
        url_entry.grid(row=8, column=0, columnspan=2, sticky="ew", pady=(2, 6))
        ttk.Button(card, text="Скопировать URL", command=self.copy_url).grid(row=9, column=0, sticky="w")

        ttk.Label(card, text="Ключ подписи", style="Card.TLabel", font=("Segoe UI Semibold", 12)).grid(
            row=10, column=0, columnspan=2, sticky="w", pady=(20, 6)
        )
        key_path = manifest.admin_private_key_path().with_name("craftunity_manifest_pub.pem")
        ttk.Label(
            card,
            text=f"Приватный ключ: {manifest.admin_private_key_path()}\nПубличный ключ: {key_path}\n"
                 "Публичный ключ должен лежать в сборке лаунчера (keys\\craftunity_manifest_pub.pem).",
            style="CardMuted.TLabel",
            justify="left",
        ).grid(row=11, column=0, columnspan=2, sticky="w")
        ttk.Button(card, text="Открыть папку ключей", command=self.open_keys_folder).grid(row=12, column=0, sticky="w", pady=(8, 0))

    # ------------------------------------------------------------------- data

    def set_status(self, text: str) -> None:
        self.status_var.set(text)

    def load_manifest(self) -> None:
        try:
            data = manifest.manifest_for_admin()
        except LauncherError as exc:
            messagebox.showerror("Craftunity Admin", str(exc))
            return
        self.manifest_data = data
        game = data.get("game") or {}
        server = data.get("server") or {}
        self.var_version.set(data.get("version") or "")
        self.var_game.set(game.get("version") or "1.20.1")
        self.var_loader.set(game.get("loader") or "fabric")
        self.var_loader_version.set(game.get("loader_version") or "")
        self.var_host.set(server.get("host") or "craftunity3.mcsh.io")
        self.var_port.set(str(server.get("port") or 25565))
        self.var_message.set(data.get("message") or "")
        launcher = data.get("launcher") or {}
        self.var_launcher_version.set(launcher.get("version") or "")
        self.var_launcher_notes.set(launcher.get("notes") or "")
        self.var_launcher_mandatory.set(bool(launcher.get("mandatory")))
        self.jvm_text.delete("1.0", "end")
        self.jvm_text.insert("1.0", " ".join((data.get("jvm") or {}).get("args") or []))
        self.message_text.delete("1.0", "end")
        self.message_text.insert("1.0", data.get("message") or "")
        self.mods = list(data.get("mods") or [])
        self.render_mods()
        self.set_status("Манифест загружен")

    def render_mods(self) -> None:
        for item in self.mods_tree.get_children():
            self.mods_tree.delete(item)
        for mod in self.mods:
            self.mods_tree.insert("", "end", values=(mod.get("project_id") or mod.get("slug") or "", mod.get("title") or ""))

    def remove_mod(self) -> None:
        selection = self.mods_tree.selection()
        if not selection:
            return
        removed: list[str] = []
        for item in selection:
            index = self.mods_tree.index(item)
            if 0 <= index < len(self.mods):
                mod = self.mods.pop(index)
                project_id = str(mod.get("project_id") or mod.get("slug") or "")
                if project_id:
                    removed.append(project_id)
        self.render_mods()
        for project_id in removed:
            self._mark_added(project_id, False)
        self.set_status("Мод убран из модпака — не забудьте опубликовать")

    def add_file_mod(self) -> None:
        path = filedialog.askopenfilename(
            title="Мод для модпака",
            filetypes=[("Minecraft мод", "*.jar")],
        )
        if not path:
            return
        self.set_status("Копирую файл в модпак…")

        def work() -> None:
            try:
                info = packstore.store_file(Path(path))
            except LauncherError as exc:
                text = str(exc)
                self.ui_queue.put(lambda: self.set_status(text))
                return
            except Exception as exc:  # noqa: BLE001
                text = f"Не удалось добавить файл: {exc}"
                self.ui_queue.put(lambda: self.set_status(text))
                return
            self.ui_queue.put(lambda: self._file_mod_added(info))

        threading.Thread(target=work, daemon=True).start()

    def _file_mod_added(self, info: dict) -> None:
        name = str(info.get("filename") or "")
        project_id = f"file:{name}"
        if any((mod.get("project_id") or mod.get("slug")) == project_id for mod in self.mods):
            self.set_status("Этот файл уже в модпаке")
            return
        self.mods.append(
            {
                "project_id": project_id,
                "slug": name,
                "title": Path(name).stem,
                "filename": name,
                "url": info.get("url") or "",
                "sha256": info.get("sha256") or "",
                "size": info.get("size") or 0,
                "version": "файл",
            }
        )
        self.render_mods()
        self.set_status("Файл добавлен в модпак — опубликуйте, чтобы он ушёл игрокам")

    def collect(self) -> dict:
        try:
            port = int(self.var_port.get().strip() or 25565)
        except ValueError:
            port = 25565
        return {
            "schema": manifest.MANIFEST_SCHEMA,
            "version": self.var_version.get().strip() or "1.0",
            "updated_at": self.manifest_data.get("updated_at", ""),
            "message": self.message_text.get("1.0", "end").strip(),
            "game": {
                "version": self.var_game.get().strip() or "1.20.1",
                "loader": "fabric",
                "loader_version": self.var_loader_version.get().strip(),
            },
            "server": {
                "host": self.var_host.get().strip() or "craftunity3.mcsh.io",
                "port": port,
                "auto_join": True,
            },
            "jvm": {
                "args": self.jvm_text.get("1.0", "end").split(),
                "java": "",
            },
            "mods": self.mods,
            "min_launcher": self.manifest_data.get("min_launcher") or "1.0.0",
            "launcher": self.collect_launcher(),
        }

    def collect_launcher(self) -> dict:
        previous = dict(self.manifest_data.get("launcher") or {})
        return {
            "version": self.var_launcher_version.get().strip(),
            "url": previous.get("url") or "",
            "sha256": previous.get("sha256") or "",
            "notes": self.var_launcher_notes.get().strip(),
            "mandatory": bool(self.var_launcher_mandatory.get()),
        }

    def save_manifest(self) -> None:
        try:
            saved = manifest.save_admin_manifest(self.collect())
        except LauncherError as exc:
            messagebox.showerror("Craftunity Admin", str(exc))
            return
        except Exception as exc:  # noqa: BLE001
            messagebox.showerror("Craftunity Admin", f"Не удалось сохранить манифест: {exc}")
            return
        self.manifest_data = saved
        self.var_version.set(saved.get("version") or "")
        target = None
        try:
            target = dropbox_store.publish(publish_dir(), saved)
        except OSError:
            target = None
        pack_note = ""
        if any(str(mod.get("project_id") or "").startswith("file:") for mod in self.mods):
            try:
                pack_note = "\n" + packstore.push()
            except LauncherError as exc:
                pack_note = f"\nФайлы модпака не ушли на сайт: {exc}"
        if target is not None:
            self.set_status(f"Опубликовано: сборка {saved.get('version')} — в папке сервера Dropbox")
            messagebox.showinfo(
                "Craftunity Admin",
                f"Сборка {saved.get('version')} опубликована и подписана.\n\n"
                f"Папка сервера: {target}\n"
                "Файлы уйдут в облако при синхронизации Dropbox — сервер в админке держать не нужно."
                + pack_note,
            )
            return
        self.set_status(f"Опубликовано: сборка {saved.get('version')} — подпись обновлена")
        messagebox.showinfo(
            "Craftunity Admin",
            f"Сборка {saved.get('version')} опубликована и подписана.\n\n"
            f"Файлы: {publish_dir()}\n"
            "Папка Dropbox не найдена — скопируйте файлы на хостинг вручную."
            + pack_note,
        )

    def save_heartbeat_receiver(self) -> None:
        save_heartbeat_config(self.var_hb_url.get(), self.var_hb_token.get())
        self.set_status("Приёмник heartbeat сохранён")
        self.refresh_clients()

    @staticmethod
    def _client_is_fresh(client: dict, now: int) -> bool:
        last_seen = int(client.get("last_seen") or 0)
        return bool(last_seen) and now - last_seen <= 180

    CONTENT_KIND_NAMES = {
        "mod": "Моды",
        "resourcepack": "Ресурспаки",
        "shader": "Шейдеры",
    }

    def _open_client_content(self) -> None:
        """Окно со списком модов, ресурспаков и шейдеров выбранного клиента."""
        selection = self.clients_tree.selection()
        if not selection:
            messagebox.showinfo("Craftunity Admin", "Выберите клиента в таблице.")
            return
        values = self.clients_tree.item(selection[0], "values") or ()
        short_id = str(values[5] or "").strip() if len(values) > 5 else ""
        username = str(values[0] or "").strip() if values else ""
        client = None
        for item in clients_for_admin():
            instance = str(item.get("instance_id") or "")
            if instance.startswith(short_id) or instance == short_id:
                client = item
                break
        if client is None:
            messagebox.showinfo("Craftunity Admin", "Запись клиента не найдена — обновите список.")
            return

        content = client.get("content")
        if not isinstance(content, list):
            messagebox.showinfo(
                "Craftunity Admin",
                f"Клиент {username or short_id} ещё не передавал состав.\n\n"
                "Лаунчер отправляет его при изменении и не чаще раза в 30 минут, "
                "начиная с версии 1.3.27.",
            )
            return

        window = tk.Toplevel(self.root)
        window.title(f"Craftunity Admin — состав клиента {username or short_id}")
        window.geometry("640x520")
        window.minsize(520, 400)
        window.configure(bg=BG)

        search_var = tk.StringVar()
        only_off_var = tk.BooleanVar(value=False)
        kind_var = tk.StringVar(value="all")

        top = ttk.Frame(window)
        top.pack(fill="x", padx=12, pady=(12, 6))
        self._clipboard(ttk.Entry(top, textvariable=search_var, width=28)).pack(side="left")
        for kind, title in (("all", "Все"), ("mod", "Моды"), ("resourcepack", "Ресурспаки"), ("shader", "Шейдеры")):
            ttk.Radiobutton(
                top, text=title, value=kind, variable=kind_var, command=lambda: render()
            ).pack(side="left", padx=(8, 0))
        ttk.Checkbutton(top, text="только выключенные", variable=only_off_var, command=lambda: render()).pack(
            side="left", padx=(12, 0)
        )

        wrap = ttk.Frame(window)
        wrap.pack(fill="both", expand=True, padx=12, pady=(0, 6))
        tree = ttk.Treeview(wrap, columns=("title", "kind", "state"), show="headings", height=16)
        for key, title, width in (("title", "НАЗВАНИЕ", 380), ("kind", "ТИП", 120), ("state", "СОСТОЯНИЕ", 110)):
            tree.heading(key, text=title)
            tree.column(key, width=width, anchor="w")
        scroll = ttk.Scrollbar(wrap, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=scroll.set)
        tree.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")

        hint = ttk.Label(window, text="", style="Muted.TLabel")
        hint.pack(anchor="w", padx=12, pady=(0, 12))

        def render() -> None:
            for item in tree.get_children():
                tree.delete(item)
            query = search_var.get().strip().lower()
            kind_filter = kind_var.get()
            only_off = bool(only_off_var.get())
            shown = 0
            counts = {"mod": 0, "resourcepack": 0, "shader": 0}
            for entry in content:
                if not isinstance(entry, dict):
                    continue
                title = str(entry.get("title") or "")
                kind = str(entry.get("kind") or "mod")
                disabled = bool(entry.get("disabled"))
                if kind in counts:
                    counts[kind] += 1
                if query and query not in title.lower():
                    continue
                if kind_filter != "all" and kind != kind_filter:
                    continue
                if only_off and not disabled:
                    continue
                tree.insert("", "end", values=(title, self.CONTENT_KIND_NAMES.get(kind, kind),
                                               "выключен" if disabled else "включён"))
                shown += 1
            hint.configure(
                text=f"Модов {counts['mod']} · ресурспаков {counts['resourcepack']} · шейдеров {counts['shader']}"
                     f" — показано {shown}"
            )

        render()
        ttk.Button(window, text="Закрыть", command=window.destroy).pack(anchor="e", padx=12, pady=(0, 12))

    def _selected_client_ids(self) -> list[str]:
        ids: list[str] = []
        for item in self.clients_tree.selection():
            values = self.clients_tree.item(item, "values") or ()
            if len(values) < 6:
                continue
            key = str(values[5] or "").strip()
            if key:
                ids.append(key)
        return ids

    def _remove_clients(self, instance_ids: list[str], question: str) -> None:
        if not instance_ids:
            messagebox.showinfo("Craftunity Admin", "Не выбрано ни одного клиента.")
            return
        if not messagebox.askyesno("Craftunity Admin", question):
            return
        removed, error = delete_clients(instance_ids)
        self.refresh_clients()
        if error:
            self.set_status(f"Удалено локально: {removed}; приёмник не ответил: {error}")
            messagebox.showwarning(
                "Craftunity Admin",
                "Клиент убран из локального списка, но приёмник heartbeat не ответил:\n"
                f"{error}\n\nЗапись вернётся при следующем чтении, если она ещё есть на приёмнике.",
            )
            return
        self.set_status(f"Удалено клиентов: {removed}")

    def delete_selected_clients(self) -> None:
        ids = self._selected_client_ids()
        if not ids:
            messagebox.showinfo("Craftunity Admin", "Выберите клиентов в таблице (Ctrl — несколько).")
            return
        self._remove_clients(ids, f"Удалить клиентов: {len(ids)}? Их записи пропадут из списка.")

    def delete_offline_clients(self) -> None:
        now = int(time.time())
        ids = [
            str(client.get("instance_id") or "")
            for client in clients_for_admin()
            if not self._client_is_fresh(client, now) and client.get("instance_id")
        ]
        if not ids:
            messagebox.showinfo("Craftunity Admin", "Офлайн-клиентов нет.")
            return
        self._remove_clients(ids, f"Удалить всех офлайн-клиентов: {len(ids)}?")

    def refresh_clients(self) -> None:
        for item in self.clients_tree.get_children():
            self.clients_tree.delete(item)
        clients = clients_for_admin()
        now = int(time.time())
        for client in clients:
            last_seen = int(client.get("last_seen") or 0)
            seen = time.strftime("%d.%m.%Y %H:%M", time.localtime(last_seen)) if last_seen else "—"
            launcher_started = int(client.get("started") or 0)
            game_started = int(client.get("game_started") or 0)
            fresh = self._client_is_fresh(client, now)
            in_game = bool(client.get("in_game")) and fresh
            if not fresh:
                online = "не в сети"
            elif in_game:
                since = game_started or last_seen
                online = f"в игре · {max(0, (now - since) // 60)} мин"
            else:
                since = launcher_started or last_seen
                online = f"в лаунчере · {max(0, (now - since) // 60)} мин"
            username = client.get("username") or ""
            avatar = self.avatars.get(username) if username else None
            row = {
                "values": (
                    username or "—",
                    client.get("version") or "—",
                    client.get("loader") or "—",
                    seen,
                    online,
                    (client.get("instance_id") or "")[:8],
                ),
            }
            if avatar is not None:
                row["image"] = avatar
            self.clients_tree.insert("", "end", **row)
        self.clients_hint.configure(text=f"Всего клиентов: {len(clients)}")
        if self._clients_timer:
            try:
                self.root.after_cancel(self._clients_timer)
            except Exception:
                pass
        self._clients_timer = self.root.after(5000, self.refresh_clients)

    def _on_avatars_ready(self) -> None:
        try:
            self.refresh_clients()
        except tk.TclError:
            pass

    # ----------------------------------------------------------------- server

    def toggle_server(self) -> None:
        if self.server and self.server.running:
            if self.beacon:
                self.beacon.stop()
                self.beacon = None
            self.server.stop()
            self.server = None
            self.server_btn.configure(text="Запустить сервер")
            self.server_state.configure(text="Сервер остановлен")
            self.url_var.set("—")
            self.set_status("Сервер манифеста остановлен")
            return
        try:
            port = int(self.var_srv_port.get().strip() or 8765)
        except ValueError:
            messagebox.showerror("Craftunity Admin", "Порт должен быть числом")
            return
        server = ManifestServer(self.var_bind.get().strip() or "127.0.0.1", port)
        try:
            url = server.start()
        except OSError as exc:
            messagebox.showerror("Craftunity Admin", f"Не удалось занять порт {port}: {exc}")
            return
        self.server = server
        self.beacon = Beacon(lambda: f"http://{lan_ip()}:{server.port}/launcher/manifest.json")
        self.beacon.start()
        self.server_btn.configure(text="Остановить сервер")
        self.server_state.configure(text=f"Сервер работает: {url} · автопоиск в сети включён")
        self.url_var.set(url)
        self.set_status("Сервер манифеста запущен, маяк включён")

    def copy_url(self) -> None:
        url = self.url_var.get()
        if url and url != "—":
            self.root.clipboard_clear()
            self.root.clipboard_append(url)
            self.set_status("URL скопирован")

    # ------------------------------------------------------------------ files

    def open_publish_folder(self) -> None:
        self._open(publish_dir())

    def open_keys_folder(self) -> None:
        self._open(manifest.admin_private_key_path().parent)

    def _open(self, path: Path) -> None:
        path.mkdir(parents=True, exist_ok=True)
        try:
            if os.name == "nt":
                os.startfile(str(path))  # type: ignore[attr-defined]
            else:
                import subprocess

                subprocess.Popen(["xdg-open", str(path)])
        except Exception as exc:  # noqa: BLE001
            messagebox.showerror("Craftunity Admin", str(exc))

    def on_close(self) -> None:
        if self.beacon:
            self.beacon.stop()
        if self.server:
            self.server.stop()
        self.root.destroy()


def main() -> int:
    root = tk.Tk()
    app = AdminApp(root)
    log_path = config.data_dir() / "admin" / "error.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)

    def report(exc_type, value, tb) -> None:
        try:
            with log_path.open("a", encoding="utf-8") as handle:
                traceback.print_exception(exc_type, value, tb, file=handle)
        except Exception:
            pass

    root.report_callback_exception = report
    try:
        with log_path.open("a", encoding="utf-8") as handle:
            handle.write("--- admin started ---\n")
        root.mainloop()
    finally:
        try:
            with log_path.open("a", encoding="utf-8") as handle:
                handle.write("--- admin closed ---\n")
        except Exception:
            pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
