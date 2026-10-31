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
from . import mods as modrinth_api
from . import palette as palette_mod
from . import skins
from .admin_server import ManifestServer, publish_dir, read_clients
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
        self.manifest_data: dict = {}
        self._clients_timer = None
        self.ui_queue: queue.Queue = queue.Queue()
        self.search_query = ""
        self.search_offset = 0
        self.search_total = 0

        root.title("Craftunity Admin")
        root.geometry("980x700")
        root.minsize(880, 620)
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

        self.tab_build = ttk.Frame(self.notebook, padding=16)
        self.tab_mods = ttk.Frame(self.notebook, padding=16)
        self.tab_clients = ttk.Frame(self.notebook, padding=16)
        self.tab_publish = ttk.Frame(self.notebook, padding=16)
        self.notebook.add(self.tab_build, text="Сборка")
        self.notebook.add(self.tab_mods, text="Модпак")
        self.notebook.add(self.tab_clients, text="Клиенты")
        self.notebook.add(self.tab_publish, text="Публикация")

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

    def _field(self, parent: ttk.Frame, row: int, column: int, label: str, variable: tk.Variable, width: int = 26) -> ttk.Entry:
        ttk.Label(parent, text=label, style="Muted.TLabel").grid(row=row * 2, column=column, sticky="w", padx=(0, 12), pady=(0, 2))
        entry = ttk.Entry(parent, textvariable=variable, width=width)
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
        entry = ttk.Entry(row, textvariable=self.var_search, width=40)
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
        columns = ("project", "title")
        self.mods_tree = ttk.Treeview(right, columns=columns, show="headings", height=14)
        self.mods_tree.heading("project", text="PROJECT ID / SLUG")
        self.mods_tree.heading("title", text="НАЗВАНИЕ")
        self.mods_tree.column("project", width=180, anchor="w")
        self.mods_tree.column("title", width=220, anchor="w")
        mods_wrap = ttk.Frame(right)
        mods_wrap.pack(fill="both", expand=True, padx=4, pady=(6, 8))
        mods_scroll = ttk.Scrollbar(mods_wrap, orient="vertical", command=self.mods_tree.yview)
        self.mods_tree.configure(yscrollcommand=mods_scroll.set)
        self.mods_tree.pack(in_=mods_wrap, side="left", fill="both", expand=True)
        mods_scroll.pack(side="right", fill="y")
        ttk.Button(right, text="Убрать выбранное", command=self.remove_mod).pack(anchor="w", padx=4)

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
        self.search_state.configure(text="Ищу в Modrinth…")

        def work() -> None:
            try:
                data = modrinth_api.search(
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
            total = int(data.get("total") or 0)
            self.ui_queue.put(lambda: self._render_results(hits, total, append))

        threading.Thread(target=work, daemon=True).start()

    def load_more(self) -> None:
        self.search_modrinth(append=True)

    def _render_results(self, hits: list[dict], total: int, append: bool = False) -> None:
        if not append:
            for child in self.results_frame.winfo_children():
                child.destroy()
            self.more_btn = None
        elif self.more_btn is not None and self.more_btn.winfo_exists():
            self.more_btn.destroy()
            self.more_btn = None
        start = len(self.results_frame.winfo_children())
        for index, hit in enumerate(hits):
            position = start + index
            card = tk.Frame(self.results_frame, bg=CARD, highlightthickness=1, highlightbackground=BORDER)
            card.grid(row=position // 3, column=position % 3, padx=6, pady=6, sticky="nsew")
            icon = tk.Label(card, text="…", bg=CARD, fg=MUTED, width=64, height=64)
            icon.pack(pady=(10, 6))
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
            ).pack(pady=(2, 6))
            ttk.Button(card, text="Добавить", command=lambda h=hit: self.add_from_hit(h)).pack(pady=(0, 10))
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
            label.configure(image=photo, text="", width=64, height=64)
            label.image = photo  # type: ignore[attr-defined]
        except Exception:
            pass

    def add_from_hit(self, hit: dict) -> None:
        project_id = hit.get("id") or hit.get("slug")
        if not project_id:
            return
        if any((mod.get("project_id") or mod.get("slug")) == project_id for mod in self.mods):
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

        columns = ("user", "version", "loader", "seen", "id")
        self.clients_tree = ttk.Treeview(self.tab_clients, columns=columns, show="tree headings", height=15)
        self.clients_tree.heading("#0", text="")
        self.clients_tree.column("#0", width=52, minwidth=52, stretch=False, anchor="center")
        for key, title, width in (
            ("user", "ИГРОК", 180),
            ("version", "ВЕРСИЯ", 120),
            ("loader", "ЗАГРУЗЧИК", 120),
            ("seen", "ПОСЛЕДНЯЯ АКТИВНОСТЬ", 220),
            ("id", "ID КЛИЕНТА", 160),
        ):
            self.clients_tree.heading(key, text=title)
            self.clients_tree.column(key, width=width, anchor="w")
        clients_wrap = ttk.Frame(self.tab_clients)
        clients_wrap.pack(fill="both", expand=True)
        clients_scroll = ttk.Scrollbar(clients_wrap, orient="vertical", command=self.clients_tree.yview)
        self.clients_tree.configure(yscrollcommand=clients_scroll.set)
        self.clients_tree.pack(in_=clients_wrap, side="left", fill="both", expand=True)
        clients_scroll.pack(side="right", fill="y")

        self.clients_hint = ttk.Label(self.tab_clients, text="Пока нет данных", style="Muted.TLabel")
        self.clients_hint.pack(anchor="w", pady=(10, 0))

    def _build_tab_publish(self) -> None:
        card = ttk.Frame(self.tab_publish, style="Card.TFrame", padding=18)
        card.pack(fill="both", expand=True)

        ttk.Label(card, text="Раздача манифеста", style="Card.TLabel", font=("Segoe UI Semibold", 12)).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 6)
        )
        ttk.Label(
            card,
            text="Включите сервер, чтобы лаунчеры могли скачать манифест по сети. Для интернета нужен HTTPS-прокси на домен.",
            style="CardMuted.TLabel",
            wraplength=780,
            justify="left",
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(0, 12))

        self.var_bind = tk.StringVar(value="127.0.0.1")
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
        self.var_host.set(server.get("host") or "craftunity.mcsh.io")
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
        for item in selection:
            index = self.mods_tree.index(item)
            if 0 <= index < len(self.mods):
                self.mods.pop(index)
        self.render_mods()
        self.set_status("Мод убран из модпака — не забудьте опубликовать")

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
                "host": self.var_host.get().strip() or "craftunity.mcsh.io",
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
        self.set_status(f"Опубликовано: сборка {saved.get('version')} — подпись обновлена")
        messagebox.showinfo(
            "Craftunity Admin",
            f"Сборка {saved.get('version')} опубликована и подписана.\n\n"
            f"Файлы: {publish_dir()}\n"
            "Лаунчеры подхватят её при следующем запуске игры.",
        )

    def refresh_clients(self) -> None:
        for item in self.clients_tree.get_children():
            self.clients_tree.delete(item)
        clients = read_clients()
        for client in clients:
            seen = time.strftime("%d.%m.%Y %H:%M", time.localtime(client.get("last_seen", 0)))
            username = client.get("username") or ""
            avatar = self.avatars.get(username) if username else None
            row = {
                "values": (
                    username or "—",
                    client.get("version") or "—",
                    client.get("loader") or "—",
                    seen,
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
