from __future__ import annotations

import json
import os
import re
import subprocess
import threading
import time
import urllib.parse
from pathlib import Path

from . import config, discovery, installer, state, updater
from .auth import ensure_authlib_injector, ely_validate
from .manifest import fallback_urls, fetch_manifest, load_cached_manifest
from .util import LauncherError, platform_token, rules_allow

PLACEHOLDER = re.compile(r"\$\{([a-z_]+)\}")


class Engine:
    def __init__(self) -> None:
        self.lock = threading.RLock()
        self.status = "idle"
        self.stage = ""
        self.progress = 0.0
        self.message = "Готов к запуску"
        self.log_lines: list[str] = []
        self.process: subprocess.Popen | None = None
        self.last_error = ""
        self.started_at = 0.0
        self.manifest: dict | None = None
        self.manifest_error = ""
        self.manifest_source = ""
        self.manifest_fetched_at = 0
        self._manifest_checked = 0.0
        self._heartbeat_discovered: tuple[str, float] | None = None
        self._content_signature = ""
        self._content_sent_at = 0.0
        self._launcher_started = int(time.time())
        self.plan: dict | None = None
        self.installed: dict | None = None
        self._thread: threading.Thread | None = None
        self._stop = threading.Event()

    # ------------------------------------------------------------------ state

    def snapshot(self) -> dict:
        with self.lock:
            return {
                "status": self.status,
                "stage": self.stage,
                "progress": round(self.progress, 4),
                "message": self.message,
                "error": self.last_error,
                "running": bool(self.process and self.process.poll() is None),
                "pid": self.process.pid if self.process and self.process.poll() is None else None,
                "log": self.log_lines[-400:],
                "manifest": self.manifest,
                "manifest_error": self.manifest_error,
                "manifest_source": self.manifest_source,
                "manifest_fetched_at": self.manifest_fetched_at,
                "started_at": self.started_at,
            }

    def _set(self, status: str | None = None, stage: str | None = None, progress: float | None = None, message: str | None = None) -> None:
        with self.lock:
            if status is not None:
                self.status = status
            if stage is not None:
                self.stage = stage
            if progress is not None:
                self.progress = max(0.0, min(1.0, progress))
            if message is not None:
                self.message = message

    def _log(self, line: str) -> None:
        with self.lock:
            self.log_lines.append(line.rstrip("\n"))
            if len(self.log_lines) > 3000:
                del self.log_lines[:1000]

    # --------------------------------------------------------------- manifest

    def refresh_manifest(self, force: bool = False) -> dict | None:
        settings = config.load_settings()
        url = settings.get("manifest_url", "")
        sig_url = str(settings.get("manifest_sig_url") or "").strip()
        now = time.time()
        with self.lock:
            if (
                not force
                and self.manifest is not None
                and self._manifest_checked
                and now - self._manifest_checked < 10
            ):
                return self.manifest
        self._manifest_checked = now
        best, best_url, errors = self._fetch_best(url, sig_url=sig_url)
        if best is not None:
            source = "network" if best_url == url else "mirror"
            with self.lock:
                self.manifest = best
                self.manifest_source = source
                self.manifest_fetched_at = int(time.time())
                self.manifest_error = ""
            if source == "mirror":
                self._log(f"[manifest] взял самую свежую копию: {best_url}")
            return best

        manifest, found_url = self._auto_discover(url)
        if manifest is not None:
            with self.lock:
                self.manifest = manifest
                self.manifest_source = "network"
                self.manifest_fetched_at = int(time.time())
                self.manifest_error = ""
            return manifest

        cached = load_cached_manifest()
        if cached:
            manifest, meta = cached
            with self.lock:
                self.manifest = manifest
                self.manifest_source = "cache"
                self.manifest_fetched_at = int(meta.get("fetched_at") or 0)
                self.manifest_error = ""
            self._log(
                "[manifest] источники недоступны, использую сохранённую версию "
                f"({self._format_ts(self.manifest_fetched_at)}): {'; '.join(errors) or url}"
            )
            return manifest

        with self.lock:
            self.manifest_error = "; ".join(errors) or f"Нет ответа: {url}"
            if self.manifest is None:
                self.manifest_source = "none"
        return self.manifest

    def _manifest_sources(self, primary: str) -> list[str]:
        sources: list[str] = []
        for candidate in (primary, config.DEFAULT_MANIFEST_URL, *fallback_urls(primary)):
            if isinstance(candidate, str) and candidate.startswith(("http://", "https://")) and candidate not in sources:
                sources.append(candidate)
        return sources

    @staticmethod
    def _freshness(manifest: dict) -> tuple:
        block = (manifest or {}).get("launcher") or {}
        updated = str((manifest or {}).get("updated_at") or "")
        return (updated, updater.version_key(str(block.get("version") or "0")))

    def _fetch_best(self, primary: str, timeout: int = 15, sig_url: str = "") -> tuple[dict | None, str, list[str]]:
        """Опрашивает все адреса и берёт самый свежий манифест: зеркала отдают кэш."""
        best: dict | None = None
        best_url = ""
        errors: list[str] = []
        for candidate in self._manifest_sources(primary):
            try:
                manifest = fetch_manifest(
                    candidate,
                    timeout=timeout,
                    sig_url=sig_url if candidate == primary else "",
                )
            except LauncherError as exc:
                errors.append(str(exc))
                continue
            if best is None or self._freshness(manifest) > self._freshness(best):
                best, best_url = manifest, candidate
        return best, best_url, errors

    def _auto_discover(self, preferred: str) -> tuple[dict | None, str]:
        candidates: list[str] = []
        discovered = discovery.discover(timeout=2.0)
        if discovered:
            candidates.append(discovered)
        candidates.append("http://127.0.0.1:8765/launcher/manifest.json")
        seen = {preferred}
        for candidate in candidates:
            if candidate in seen:
                continue
            seen.add(candidate)
            try:
                manifest = fetch_manifest(candidate)
            except LauncherError:
                continue
            try:
                config.save_settings({"manifest_url": candidate})
            except Exception:
                pass
            self._log(f"[manifest] найден автоматически: {candidate}")
            return manifest, candidate
        return None, ""

    @staticmethod
    def _format_ts(value: int) -> str:
        if not value:
            return "неизвестно"
        return time.strftime("%d.%m.%Y %H:%M", time.localtime(value))

    # ---------------------------------------------------------------- install

    def prepare(self, force: bool = False, deep: bool = False) -> dict:
        settings = config.load_settings()
        manifest = self.refresh_manifest() or self.manifest
        game = (manifest or {}).get("game") or {}
        version = game.get("version") or settings["game_version"]
        loader = "fabric"
        loader_version = game.get("loader_version") or settings.get("loader_version") or ""

        inst = installer.Installer(self._progress)
        self._set("installing", "plan", 0.02, "Проверка версии клиента…")
        plan = inst.build_plan(version, loader, loader_version)
        with self.lock:
            self.plan = plan

        expected = self._required_files(plan)
        ready = not force and not deep and all(path.exists() for path in expected)
        if ready:
            fixed = inst.verify_files(plan)
            if fixed["libraries"] or fixed["assets"]:
                self._log(
                    f"[files] восстановлено: библиотек {fixed['libraries']}, ресурсов {fixed['assets']}"
                )
            with self.lock:
                self.plan = plan
                self.installed = self._installed_info(plan)
            self._set("ready", "files", 1.0, "Клиент готов")
        else:
            info = inst.install(plan, force_assets=deep)
            with self.lock:
                self.installed = info
            self._set("ready", "files", 1.0, "Клиент готов")

        from . import mods as mods_module

        self._set("installing", "mods", 0.0, f"Подгоняю моды под {version}…")
        sync = mods_module.sync_for_version(version, loader)
        if sync["updated"]:
            self._log(f"[mods] обновлены под {version}: {', '.join(sync['updated'])}")
        if sync["removed"]:
            self._log(f"[mods] удалены (нет сборки под {version}): {', '.join(sync['removed'])}")
        for failure in sync["failed"]:
            self._log(f"[mods] {failure['project']}: {failure['error']}")

        mods_manifest = (manifest or {}).get("mods") or []
        if mods_manifest:
            self._set("installing", "mods", 0.0, "Проверка модпака сервера…")
            results = mods_module.sync_manifest_mods(manifest or {}, version, loader)
            failed = [r for r in results if r.get("error")]
            if failed:
                self._log(f"[manifest] Не удалось установить {len(failed)} мод(ов) модпака")

        self._prepare_authlib()
        return {"version": version, "loader": loader, "loader_version": loader_version, "install_id": plan["_install_id"]}

    def _required_files(self, plan: dict) -> list[Path]:
        version_dir = Path(config.game_dir()) / "versions" / plan["_install_id"]
        checks = [version_dir / f"{plan['_install_id']}.json", version_dir / f"{plan['_install_id']}.jar"]
        index = (plan.get("assetIndex") or {}).get("id")
        if index:
            checks.append(Path(config.game_dir()) / "assets" / "indexes" / f"{index}.json")
        return checks

    def _installed_info(self, plan: dict) -> dict:
        version_dir = Path(config.game_dir()) / "versions" / plan["_install_id"]
        inst = installer.Installer()
        _artifacts, _natives, classpath = inst._collect(plan)
        return {
            "version_dir": version_dir,
            "client_jar": version_dir / f"{plan['_install_id']}.jar",
            "classpath": [item["path"] for item in classpath],
            "natives_dir": version_dir / "natives",
            "assets_dir": Path(config.game_dir()) / "assets",
            "asset_index": (plan.get("assetIndex") or {}).get("id") or "legacy",
            "logging": None,
            "logging_path": None,
        }

    def _prepare_authlib(self) -> None:
        account = state.current_account()
        if not account or account.get("type") != "ely":
            return
        try:
            ensure_authlib_injector()
        except LauncherError as exc:
            self._log(f"[auth] authlib-injector недоступен: {exc}")

    def _progress(self, stage: str, done: int, total: int, message: str) -> None:
        weight = {"plan": 0.02, "libraries": 0.45, "natives": 0.5, "assets": 0.9, "mods": 0.95}
        base = weight.get(stage, 0.5)
        span = 0.05 if stage in ("plan", "natives", "java") else 0.4
        ratio = done / max(total, 1)
        self._set("installing", stage, min(0.99, base + span * ratio), message)

    # ----------------------------------------------------------------- launch

    def launch(self) -> dict:
        with self.lock:
            if self.process and self.process.poll() is None:
                raise LauncherError("Игра уже запущена.")
            self._stop.clear()
            self.log_lines = []
            self.last_error = ""
            self.status = "starting"
        self._thread = threading.Thread(target=self._launch_worker, daemon=True)
        self._thread.start()
        return {"started": True}

    def _launch_worker(self) -> None:
        try:
            self._set("starting", "prepare", 0.02, "Подготовка клиента…")
            info = self.prepare()
            self._set("starting", "auth", 0.9, "Проверка аккаунта…")
            account = state.current_account()
            if not account:
                raise LauncherError("Сначала войдите в аккаунт: оффлайн-ник или ely.by.")
            if account.get("type") == "ely":
                if not ely_validate(account):
                    raise LauncherError(
                        "Сессия ely.by истекла или недействительна. "
                        "Войдите в аккаунт заново на вкладке «Аккаунт»."
                    )
                account = state.current_account() or account
            settings = config.load_settings()
            required = installer.required_java_major(info["version"])
            java = settings.get("java_path") or installer.detect_java(required)
            major = installer.java_major(java) if java else 0
            if not java or major < required:
                from . import java as java_module

                cached = java_module.cached_runtime(required)
                if cached:
                    java = cached
                    major = installer.java_major(java)
            if not java or major < required:
                if settings.get("auto_java", True):
                    from . import java as java_module

                    self._set("installing", "java", 0.96, f"Готовлю Java {required}…")
                    java = java_module.ensure_runtime(required, self._progress)
                    major = installer.java_major(java)
                else:
                    raise LauncherError(
                        f"Java {required}+ не найдена. Установите её или включите автозагрузку в настройках."
                    )
            if not java or (major and major < required):
                raise LauncherError(
                    f"Найдена Java {major or '—'}, а для Minecraft {info['version']} нужна Java {required}+."
                )
            self._log(f"[launcher] Java подходит: {major} ({java})")
            self._set("starting", "launch", 0.97, "Запуск Minecraft…")
            self.started_at = time.time()
            command = self.build_command(account, java)
            log_path = Path(config.game_dir()) / "logs" / f"craftunity-{time.strftime('%Y%m%d-%H%M%S')}.log"
            log_path.parent.mkdir(parents=True, exist_ok=True)
            log_handle = log_path.open("w", encoding="utf-8", errors="replace")
            flags = 0
            detached = os.name == "nt"
            if detached:
                flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
            else:
                flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            process = subprocess.Popen(
                command,
                cwd=str(config.game_dir()),
                stdin=subprocess.DEVNULL,
                stdout=log_handle,
                stderr=subprocess.STDOUT,
                creationflags=flags,
                start_new_session=not detached,
            )
            with self.lock:
                self.process = process
            self._set("running", "game", 1.0, "Minecraft запущен")
            state.last_played(info["version"])
            self._log(f"[launcher] Java: {java}")
            self._log(f"[launcher] Лог: {log_path}")
            threading.Thread(target=self._pump, args=(process, log_handle), daemon=True).start()
            self._heartbeat(info)
        except LauncherError as exc:
            self._fail(str(exc))
        except Exception as exc:  # noqa: BLE001
            self._fail(f"Непредвиденная ошибка: {exc}")

    def _fail(self, message: str) -> None:
        with self.lock:
            self.status = "error"
            self.last_error = message
            self.message = message
            self.progress = 0.0
            self.log_lines.append(f"[error] {message}")

    def _pump(self, process: subprocess.Popen, log_handle) -> None:
        try:
            code = process.wait()
        except Exception:  # noqa: BLE001
            code = -1
        finally:
            try:
                log_handle.close()
            except Exception:  # noqa: BLE001
                pass
            with self.lock:
                if self.status != "error":
                    self.status = "idle" if code == 0 else "error"
                    self.message = "Игра закрыта" if code == 0 else f"Minecraft завершился с кодом {code}"
                    if code != 0:
                        self.last_error = self.message

    def report_presence(self) -> None:
        """Отправляет админке сведения о клиенте без запуска игры."""
        manifest = self.manifest or self.refresh_manifest()
        game = (manifest or {}).get("game") or {}
        settings = config.load_settings()
        self._heartbeat(
            {
                "version": game.get("version") or settings.get("game_version"),
                "loader": "fabric",
            }
        )

    @staticmethod
    def _endpoint(url: str) -> str:
        """Приводит адрес к .../heartbeat: в настройках и манифесте часто указан только домен."""
        parsed = urllib.parse.urlparse(str(url or "").strip())
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            return ""
        path = parsed.path.rstrip("/")
        if not path:
            path = "/heartbeat"
        return urllib.parse.urlunparse((parsed.scheme, parsed.netloc, path, "", parsed.query, ""))

    def _heartbeat_target(self, discovered: bool = True) -> str:
        """Сначала внешний приёмник, затем админка в локальной сети."""
        settings = config.load_settings()
        url = (settings.get("heartbeat_url") or "").strip()
        if url:
            return self._endpoint(url)
        published = str((self.manifest or {}).get("heartbeat_url") or "").strip()
        if published.startswith(("http://", "https://")):
            return self._endpoint(published)
        found = self._discover_heartbeat() if discovered else ""
        if found:
            return found
        manifest_url = (settings.get("manifest_url") or "").strip()
        parsed = urllib.parse.urlparse(manifest_url)
        if (
            parsed.scheme in ("http", "https")
            and parsed.netloc
            and not parsed.netloc.endswith("github.io")
        ):
            return f"{parsed.scheme}://{parsed.netloc}/heartbeat"
        return ""

    def _heartbeat_fallbacks(self, url: str) -> list[str]:
        """Куда отправить присутствие, если основной приёмник не отвечает."""
        targets: list[str] = []
        local = self._discover_heartbeat(force=True)
        if local and local != url:
            targets.append(local)
        settings = config.load_settings()
        manifest_url = (settings.get("manifest_url") or "").strip()
        parsed = urllib.parse.urlparse(manifest_url)
        if parsed.scheme in ("http", "https") and parsed.netloc and "dropbox.com" not in parsed.netloc:
            own = f"{parsed.scheme}://{parsed.netloc}/heartbeat"
            if own not in targets and own != url:
                targets.append(own)
        return targets

    def _discover_heartbeat(self, force: bool = False) -> str:
        """Адрес админки из UDP-маяка: работает при любой смене LAN-адреса."""
        now = time.time()
        if not force and self._heartbeat_discovered:
            url, stamp = self._heartbeat_discovered
            if now - stamp < 600:
                return url
        found = discovery.discover(timeout=1.5)
        target = ""
        if found:
            parsed = urllib.parse.urlparse(found)
            if parsed.scheme in ("http", "https") and parsed.netloc:
                target = f"{parsed.scheme}://{parsed.netloc}/heartbeat"
        self._heartbeat_discovered = (target, now) if target else None
        return target

    def _post_heartbeat(self, url: str, payload: dict) -> bool:
        try:
            from .util import http_json

            http_json(url, payload=payload, method="POST", timeout=8)
            return True
        except Exception:
            return False

    def _content_snapshot(self) -> list[dict]:
        """Состав модов, ресурспаков и шейдеров для админки: только названия, тип и флаг."""
        from . import mods as mods_module

        items = mods_module.installed_all().get("items") or []
        result = []
        for item in items:
            kind = item.get("kind") or "mod"
            title = str(item.get("title") or item.get("slug") or item.get("filename") or "").strip()
            if not title:
                continue
            result.append({"title": title[:80], "kind": kind, "disabled": bool(item.get("disabled"))})
        result.sort(key=lambda entry: (entry["kind"], entry["title"].lower()))
        return result[:400]

    def _content_for_heartbeat(self) -> list[dict] | None:
        """Состав отправляется при изменении и не чаще раза в 30 минут."""
        try:
            snapshot = self._content_snapshot()
        except Exception:
            return None
        import hashlib
        import json as json_module

        signature = hashlib.sha1(
            json_module.dumps(snapshot, ensure_ascii=False, sort_keys=True).encode("utf-8")
        ).hexdigest()
        now = time.time()
        if self._content_signature == signature and now - self._content_sent_at < 1800:
            return None
        self._content_signature = signature
        self._content_sent_at = now
        return snapshot

    def _heartbeat(self, info: dict) -> None:
        account = state.current_account() or {}
        username = account.get("username")
        icon = ""
        if username:
            icon = "https://skinsystem.ely.by/skins/" + urllib.parse.quote(str(username))
        payload = {
            "instance_id": state.instance_id(),
            "version": info.get("version"),
            "loader": info.get("loader"),
            "username": username,
            "icon": icon,
            "started": self._launcher_started,
            "game_started": int(self.started_at or 0),
            "in_game": bool(self.process and self.process.poll() is None),
            "launched": bool(self.started_at),
        }
        content = self._content_for_heartbeat()
        if content is not None:
            payload["content"] = content
        url = self._heartbeat_target()
        if not url:
            return
        if self._post_heartbeat(url, payload):
            return
        for fallback in self._heartbeat_fallbacks(url):
            if self._post_heartbeat(fallback, payload):
                return

    # ---------------------------------------------------------------- command

    def build_command(self, account: dict, java: str) -> list[str]:
        plan = self.plan
        info = self.installed
        if not plan or not info:
            raise LauncherError("Клиент ещё не установлен.")
        settings = config.load_settings()
        memory = int(settings.get("memory_mb") or config.DEFAULT_MEMORY_MB)
        game_dir = Path(config.game_dir())
        natives = Path(info["natives_dir"])
        classpath = os.pathsep.join([str(info["client_jar"])] + info["classpath"])
        assets_root = Path(info["assets_dir"])
        asset_index = info["asset_index"]
        version_name = plan["_install_id"]

        ely = account.get("type") == "ely"
        values = {
            "auth_player_name": account.get("username", "Player"),
            "version_name": version_name,
            "game_directory": str(game_dir),
            "assets_root": str(assets_root),
            "game_assets": str(assets_root / "virtual" / asset_index),
            "assets_index_name": asset_index,
            "auth_uuid": account.get("uuid", ""),
            "auth_access_token": account.get("access_token", "0" * 32),
            "auth_session": account.get("access_token", "0" * 32),
            "clientid": "",
            "auth_xuid": "",
            "user_type": "mojang" if ely else "legacy",
            "user_properties": "{}",
            "natives_directory": str(natives),
            "launcher_name": "Craftunity",
            "launcher_version": config.LAUNCHER_VERSION,
            "classpath": classpath,
            "classpath_separator": os.pathsep,
            "library_directory": str(Path(config.game_dir()) / "libraries"),
            "version_type": "release",
            "resolution_width": "1280",
            "resolution_height": "720",
            "quickPlayPath": "",
            "quickPlaySingleplayer": "",
            "quickPlayMultiplayer": "",
            "quickPlayRealms": "",
        }

        def substitute(value):
            if isinstance(value, dict):
                return value.get("value")
            return value

        jvm_args = ["-Xmx%dm" % memory, "-Xms%dm" % min(1024, memory)]
        for entry in (plan.get("arguments") or {}).get("jvm") or []:
            if isinstance(entry, dict):
                if not rules_allow(entry.get("rules")):
                    continue
                value = entry.get("value")
            else:
                value = entry
            for item in value if isinstance(value, list) else [value]:
                rendered = self._render(item, values)
                if rendered and "heapdump" not in rendered.lower():
                    jvm_args.append(rendered)

        if not any(arg.startswith("-Djava.library.path") for arg in jvm_args):
            jvm_args.append(f"-Djava.library.path={natives}")
        jvm_args.append(f"-Dcraftunity.version={plan['_game_version']}")

        if ely:
            jar = ensure_authlib_injector()
            jvm_args.append(f"-javaagent:{jar}={config.ELY_AUTH_ROOT}")
            jvm_args.append("-Dauthlibinjector.side=client")

        for extra in ((self.manifest or {}).get("jvm") or {}).get("args") or []:
            if isinstance(extra, str) and extra.startswith("-X") and not extra.startswith("-Xmx"):
                jvm_args.append(extra)

        command = [java] + jvm_args
        logging_info = info.get("logging")
        if logging_info and info.get("logging_path"):
            command.append(self._render(logging_info, {**values, "path": str(info["logging_path"])}))
        command += ["-cp", classpath, plan.get("mainClass", "net.minecraft.client.main.Main")]

        game_args: list[str] = []
        for entry in (plan.get("arguments") or {}).get("game") or []:
            if isinstance(entry, dict):
                if not rules_allow(entry.get("rules")):
                    continue
                value = entry.get("value")
            else:
                value = entry
            for item in value if isinstance(value, list) else [value]:
                if item is None:
                    continue
                rendered = self._render(item, values)
                game_args.append(rendered)

        if not game_args and plan.get("minecraftArguments"):
            game_args = [self._render(token, values) for token in plan["minecraftArguments"].split(" ")]

        server = (self.manifest or {}).get("server") or {}
        host = server.get("host") or settings.get("server_host")
        port = int(server.get("port") or settings.get("server_port") or 25565)
        auto_join = server.get("auto_join", settings.get("auto_join", True)) and settings.get("auto_join", True)
        if auto_join and host:
            if self._supports_quick_play(plan.get("_game_version", "")):
                game_args += ["--quickPlayMultiplayer", f"{host}:{port}"]
            else:
                game_args += ["--server", host, "--port", str(port)]

        command += game_args
        return command

    @staticmethod
    def _supports_quick_play(version: str) -> bool:
        try:
            major, minor = (int(x) for x in version.split(".")[:2])
        except (ValueError, IndexError):
            return True
        return major > 1 or (major == 1 and minor >= 20)

    @staticmethod
    def _render(value: str, values: dict) -> str:
        if not isinstance(value, str):
            return value
        return PLACEHOLDER.sub(lambda m: str(values.get(m.group(1), m.group(0))), value)

    # ------------------------------------------------------------------- stop

    def kill(self) -> dict:
        with self.lock:
            process = self.process
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill()
            self._set("idle", "stopped", 0.0, "Игра остановлена")
            return {"stopped": True}
        return {"stopped": False}
