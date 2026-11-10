from __future__ import annotations

import copy
import json
import os
import platform
import shutil
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from typing import Callable

from . import config
from .util import (
    LauncherError,
    download,
    extract_natives,
    http_json,
    maven_to_path,
    platform_token,
    rules_allow,
    sha1_file,
)

ProgressFn = Callable[[str, int, int, str], None]


class Installer:
    def __init__(self, progress: ProgressFn | None = None) -> None:
        self.progress = progress or (lambda stage, done, total, message: None)
        self.game = config.game_dir()
        self.versions = self.game / "versions"
        self.libraries = self.game / "libraries"
        self.assets = self.game / "assets"
        self.logs = self.game / "logs"
        for path in (self.versions, self.libraries, self.assets, self.logs):
            path.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._done = 0
        self._total = 1

    def _tick(self, stage: str, message: str) -> None:
        with self._lock:
            self._done += 1
            done, total = self._done, self._total
        self.progress(stage, done, total, message)

    def _set_total(self, stage: str, total: int, message: str) -> None:
        with self._lock:
            self._done = 0
            self._total = max(total, 1)
        self.progress(stage, 0, max(total, 1), message)

    # ---------------------------------------------------------------- metadata

    def version_manifest(self, force: bool = False) -> dict:
        cache = config.cache_dir() / "version_manifest_v2.json"
        if not force and cache.exists() and time.time() - cache.stat().st_mtime < 3600:
            try:
                return json.loads(cache.read_text(encoding="utf-8"))
            except ValueError:
                pass
        data = http_json(config.VERSION_MANIFEST_URL, timeout=30)
        cache.write_text(json.dumps(data), encoding="utf-8")
        return data

    def resolve_version(self, version_id: str) -> dict:
        manifest = self.version_manifest()
        entry = next((v for v in manifest.get("versions", []) if v.get("id") == version_id), None)
        if not entry:
            raise LauncherError(f"Версия Minecraft «{version_id}» не найдена у Mojang.")
        cached = config.cache_dir() / "versions" / f"{version_id}.json"
        cached.parent.mkdir(parents=True, exist_ok=True)
        if cached.exists() and time.time() - cached.stat().st_mtime < 86400:
            try:
                return json.loads(cached.read_text(encoding="utf-8"))
            except ValueError:
                pass
        data = http_json(entry["url"], timeout=30)
        cached.write_text(json.dumps(data), encoding="utf-8")
        return data

    def latest_fabric_loader(self, game_version: str) -> str:
        data = http_json(f"{config.FABRIC_META}/versions/loader/{game_version}", timeout=20)
        stable = [item for item in data if item.get("loader", {}).get("stable")]
        chosen = (stable or data)[0]
        return chosen["loader"]["version"]

    def fabric_profile(self, game_version: str, loader_version: str) -> dict:
        url = f"{config.FABRIC_META}/versions/loader/{game_version}/{loader_version}/profile/json"
        return http_json(url, timeout=30)

    def build_plan(self, game_version: str, loader: str, loader_version: str = "") -> dict:
        vanilla = self.resolve_version(game_version)
        plan = copy.deepcopy(vanilla)
        plan_id = game_version
        if loader == "fabric":
            if not loader_version:
                loader_version = self.latest_fabric_loader(game_version)
            fabric = self.fabric_profile(game_version, loader_version)
            plan_id = f"{game_version}-fabric-{loader_version}"
            plan["id"] = plan_id
            plan["mainClass"] = fabric.get("mainClass", plan.get("mainClass"))
            plan["libraries"] = list(fabric.get("libraries") or []) + list(plan.get("libraries") or [])
            vanilla_args = plan.get("arguments") or {}
            fabric_args = fabric.get("arguments") or {}
            plan["arguments"] = {
                "game": list(vanilla_args.get("game") or []) + list(fabric_args.get("game") or []),
                "jvm": list(vanilla_args.get("jvm") or []) + list(fabric_args.get("jvm") or []),
            }
            plan["fabric"] = {"loader_version": loader_version}
            plan["inheritsFrom"] = game_version
        plan["_install_id"] = plan_id
        plan["_loader"] = loader
        plan["_loader_version"] = loader_version
        plan["_game_version"] = game_version
        return plan

    # ------------------------------------------------------------------- files

    def _artifact_for(self, lib: dict) -> dict:
        downloads = lib.get("downloads") or {}
        artifact = downloads.get("artifact")
        if artifact and artifact.get("url"):
            return artifact
        name = lib.get("name") or ""
        if name and lib.get("url"):
            path = maven_to_path(name)
            return {
                "path": path,
                "url": lib["url"].rstrip("/") + "/" + path,
                "sha1": "",
                "size": 0,
            }
        return {}

    def _natives_for(self, lib: dict) -> tuple[dict, str] | None:
        natives = lib.get("natives") or {}
        key = natives.get(platform_token())
        if not key:
            return None
        key = key.replace("${arch}", "64" if sys.maxsize > 2**32 else "32")
        artifact = (lib.get("downloads") or {}).get("classifiers", {}).get(key)
        if not artifact:
            return None
        return artifact, key

    def _collect(self, plan: dict) -> tuple[list[tuple[dict, Path]], list[tuple[dict, Path]], list[dict]]:
        base = config.cache_dir() / "libraries"
        artifacts: list[tuple[dict, Path]] = []
        natives: list[tuple[dict, Path]] = []
        classpath: list[dict] = []
        seen: set[str] = set()
        for lib in plan.get("libraries") or []:
            if not rules_allow(lib.get("rules")):
                continue
            artifact = self._artifact_for(lib)
            if artifact and artifact.get("path"):
                target = self.libraries / artifact["path"]
                if artifact["path"] not in seen:
                    seen.add(artifact["path"])
                    artifacts.append((artifact, target))
                    classpath.append({"path": str(target), "name": lib.get("name", "")})
            native = self._natives_for(lib)
            if native:
                native_artifact, _key = native
                target = self.libraries / native_artifact["path"]
                natives.append((native_artifact, target))
        return artifacts, natives, classpath

    def install(self, plan: dict, force_assets: bool = False) -> dict:
        install_id = plan["_install_id"]
        version_dir = self.versions / install_id
        version_dir.mkdir(parents=True, exist_ok=True)
        version_json_path = version_dir / f"{install_id}.json"
        clean_plan = {k: v for k, v in plan.items() if not k.startswith("_")}
        version_json_path.write_text(json.dumps(clean_plan, ensure_ascii=False, indent=2), encoding="utf-8")

        self.progress("plan", 0, 1, "Подготовка файлов клиента…")

        client = (plan.get("downloads") or {}).get("client") or {}
        client_jar = version_dir / f"{install_id}.jar"
        if client.get("url"):
            download(client["url"], client_jar, client.get("sha1"), client.get("size"))
        elif not client_jar.exists():
            vanilla_jar = self.versions / plan["_game_version"] / f"{plan['_game_version']}.jar"
            if vanilla_jar.exists():
                shutil.copyfile(vanilla_jar, client_jar)

        artifacts, natives, classpath = self._collect(plan)
        self._set_total("libraries", len(artifacts) + len(natives), "Скачивание библиотек…")

        def fetch(item: tuple[dict, Path]) -> None:
            artifact, target = item
            try:
                download(artifact["url"], target, artifact.get("sha1") or None, artifact.get("size") or None)
            except LauncherError:
                raise
            self._tick("libraries", target.name)

        failures: list[str] = []
        with ThreadPoolExecutor(max_workers=8) as pool:
            futures = [pool.submit(fetch, item) for item in artifacts]
            for future in futures:
                try:
                    future.result()
                except LauncherError as exc:
                    failures.append(str(exc))
        if failures:
            raise LauncherError(failures[0])

        natives_dir = version_dir / "natives"
        if natives_dir.exists():
            shutil.rmtree(natives_dir, ignore_errors=True)
        natives_dir.mkdir(parents=True, exist_ok=True)
        for artifact, target in natives:
            if not target.exists():
                download(artifact["url"], target)
            extract_natives(target, natives_dir)
            self._tick("natives", target.name)

        self._install_assets(plan, force_assets)

        logging_cfg = (plan.get("logging") or {}).get("client") or {}
        logging_file = logging_cfg.get("file") or {}
        logging_path = None
        if logging_file.get("url"):
            safe_name = str(logging_file.get("id") or "client.xml").replace("/", "_")
            logging_path = self.assets / "log_configs" / safe_name
            download(logging_file["url"], logging_path, logging_file.get("sha1"), logging_file.get("size"))

        asset_index_id = ((plan.get("assetIndex") or {}).get("id")) or "legacy"
        return {
            "version_dir": version_dir,
            "client_jar": client_jar,
            "classpath": [item["path"] for item in classpath],
            "natives_dir": natives_dir,
            "assets_dir": self.assets,
            "asset_index": asset_index_id,
            "logging": logging_cfg.get("argument") if logging_path else None,
            "logging_path": logging_path,
        }

    def _install_assets(self, plan: dict, force: bool = False) -> None:
        asset_index = plan.get("assetIndex") or {}
        if not asset_index.get("url"):
            return
        index_id = asset_index.get("id") or "legacy"
        index_path = self.assets / "indexes" / f"{index_id}.json"
        download(asset_index["url"], index_path, asset_index.get("sha1"), asset_index.get("size"))
        try:
            index = json.loads(index_path.read_text(encoding="utf-8"))
        except ValueError as exc:
            raise LauncherError("Индекс ресурсов Minecraft повреждён.") from exc
        objects = index.get("objects") or {}
        self._set_total("assets", len(objects), f"Проверка ресурсов ({len(objects)} файлов)…")

        def fetch(item: tuple[str, dict]) -> None:
            rel, meta = item
            digest = meta["hash"]
            target = self.assets / "objects" / digest[:2] / digest
            if target.exists() and (force is False) and target.stat().st_size == meta.get("size", 0):
                self._tick("assets", rel)
                return
            download(
                f"https://resources.download.minecraft.net/{digest[:2]}/{digest}",
                target,
                digest,
                meta.get("size"),
            )
            self._tick("assets", rel)

        with ThreadPoolExecutor(max_workers=12) as pool:
            list(pool.map(fetch, list(objects.items())))

        if index.get("virtual"):
            virtual_dir = self.assets / "virtual" / index_id
            for rel, meta in objects.items():
                target = self.assets / "objects" / meta["hash"][:2] / meta["hash"]
                dest = virtual_dir / rel
                if target.exists() and not dest.exists():
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(target, dest)
        if index.get("map_to_resources"):
            for rel, meta in objects.items():
                target = self.assets / "objects" / meta["hash"][:2] / meta["hash"]
                dest = self.game / "resources" / rel
                if target.exists() and not dest.exists():
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(target, dest)


    def verify_files(self, plan: dict, deep: bool = False) -> dict:
        """Докачивает недостающие библиотеки, нативы, клиент и объекты ресурсов."""
        repaired_libraries = 0
        artifacts, natives, _classpath = self._collect(plan)

        def ok(artifact: dict, target: Path) -> bool:
            if not target.exists():
                return False
            size = artifact.get("size")
            return not size or target.stat().st_size == size

        for artifact, target in artifacts + natives:
            expected = artifact.get("sha1") or ""
            valid = ok(artifact, target)
            if valid and deep and expected:
                valid = sha1_file(target) == expected
            if valid:
                continue
            download(artifact["url"], target, expected or None, artifact.get("size") or None)
            repaired_libraries += 1

        repaired_assets = 0
        index_id = (plan.get("assetIndex") or {}).get("id")
        index_path = self.assets / "indexes" / f"{index_id}.json"
        if index_id and index_path.exists():
            try:
                objects = (json.loads(index_path.read_text(encoding="utf-8")).get("objects") or {})
            except ValueError:
                objects = {}
            missing = []
            for meta in objects.values():
                target = self.assets / "objects" / meta["hash"][:2] / meta["hash"]
                if not target.exists() or target.stat().st_size != meta.get("size", 0):
                    missing.append(meta)
                elif deep and sha1_file(target) != meta["hash"]:
                    missing.append(meta)
            if missing:
                self._set_total("assets", len(missing), f"Восстановление ресурсов ({len(missing)})…")

                def fetch(meta: dict) -> None:
                    digest = meta["hash"]
                    target = self.assets / "objects" / digest[:2] / digest
                    download(
                        f"https://resources.download.minecraft.net/{digest[:2]}/{digest}",
                        target,
                        digest,
                        meta.get("size"),
                    )
                    self._tick("assets", digest)

                with ThreadPoolExecutor(max_workers=12) as pool:
                    list(pool.map(fetch, missing))
                repaired_assets = len(missing)

        client = (plan.get("downloads") or {}).get("client") or {}
        client_jar = self.versions / plan["_install_id"] / f"{plan['_install_id']}.jar"
        client_valid = bool(client.get("url")) and client_jar.exists() and not (
            client.get("size") and client_jar.stat().st_size != client["size"]
        )
        if client_valid and deep and client.get("sha1"):
            client_valid = sha1_file(client_jar) == client["sha1"]
        if client.get("url") and not client_valid:
            download(client["url"], client_jar, client.get("sha1"), client.get("size"))
            repaired_libraries += 1

        return {"libraries": repaired_libraries, "assets": repaired_assets}


def java_candidates() -> list[Path]:
    """Все найденные java.exe: свой рантайм лаунчера первым, остальные — по порядку."""
    candidates: list[Path] = []
    explicit = os.environ.get("CRAFTUNITY_JAVA")
    if explicit and Path(explicit).exists():
        candidates.append(Path(explicit))
    own = config.data_dir() / "runtime"
    if own.exists():
        candidates.extend(sorted(own.glob("**/bin/java.exe"), reverse=True))
    java_home = os.environ.get("JAVA_HOME")
    if java_home:
        candidates.append(Path(java_home) / "bin" / "java.exe")
    for base in (
        Path("C:/Program Files/Eclipse Adoptium"),
        Path("C:/Program Files/Microsoft"),
        Path("C:/Program Files/BellSoft"),
        Path("C:/Program Files/Zulu"),
        Path(os.environ.get("APPDATA", "")) / ".minecraft" / "runtime",
        Path("C:/Program Files/Java"),
    ):
        if base.exists():
            candidates.extend(sorted(base.glob("**/bin/java.exe"), reverse=True))
    which = shutil.which("java")
    if which:
        candidates.append(Path(which))
    seen: set[str] = set()
    unique: list[Path] = []
    for candidate in candidates:
        if not candidate:
            continue
        key = str(candidate).lower()
        if key in seen or not candidate.exists():
            continue
        seen.add(key)
        unique.append(candidate)
    return unique


def detect_java(required: int = 0) -> str:
    """Ищет Java подходящей версии; при required=0 возвращает первую найденную."""
    for candidate in java_candidates():
        if required <= 0 or java_major(str(candidate)) >= required:
            return str(candidate)
    return ""


def java_major(java_path: str) -> int:
    if not java_path:
        return 0
    import subprocess

    try:
        result = subprocess.run(
            [java_path, "-version"],
            capture_output=True,
            text=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        text = (result.stderr or "") + (result.stdout or "")
        for marker in ('version "', "version "):
            if marker in text:
                version = text.split(marker, 1)[1].split('"')[0].split()[0]
                major = version.split(".")[0]
                if major == "1":
                    return int(version.split(".")[1])
                return int("".join(ch for ch in major if ch.isdigit()) or 0)
    except Exception:
        return 0
    return 0


def required_java_major(game_version: str) -> int:
    parts = [chunk for chunk in str(game_version or "").split(".") if chunk.isdigit()]
    if not parts:
        return 21
    # «1.16.5» — старая схема версий, «26.2» — новая: у неё мажор и есть версия игры.
    if parts[0] == "1" and len(parts) > 1:
        major = int(parts[1])
    else:
        major = int(parts[0])
    if major >= 24:
        return 25
    if major >= 20:
        return 21
    if major >= 18:
        return 17
    if major >= 17:
        return 16
    return 8
