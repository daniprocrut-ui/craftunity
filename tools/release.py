from __future__ import annotations

import argparse
import hashlib
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from craftunity import config, manifest  # noqa: E402

CONFIG_PATH = ROOT / "craftunity" / "config.py"
DIST_EXE = ROOT / "dist" / "Craftunity_launcher.exe"
SITE_DIR = ROOT / "docs"
LAUNCHER_DIR = SITE_DIR / "launcher"
ASSET_NAME = "Craftunity_launcher.exe"
KEEP_BUILDS = 2


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for piece in iter(lambda: handle.read(256 * 1024), b""):
            digest.update(piece)
    return digest.hexdigest()


def current_version() -> str:
    found = re.search(r'LAUNCHER_VERSION\s*=\s*"([^"]+)"', CONFIG_PATH.read_text(encoding="utf-8"))
    if not found:
        raise SystemExit("Не нашёл LAUNCHER_VERSION в craftunity/config.py")
    return found.group(1)


def bump_version(version: str) -> None:
    text = CONFIG_PATH.read_text(encoding="utf-8")
    updated = re.sub(r'(LAUNCHER_VERSION\s*=\s*)"[^"]+"', rf'\g<1>"{version}"', text, count=1)
    if updated == text:
        print(f"config.py уже на версии {version}")
    else:
        CONFIG_PATH.write_text(updated, encoding="utf-8")
        print(f"config.py: LAUNCHER_VERSION → {version}")


def run(command: list[str], **kwargs) -> None:
    print("$ " + " ".join(command))
    subprocess.run(command, check=True, **kwargs)


def repo_slug(explicit: str | None) -> str:
    if explicit:
        return explicit
    try:
        url = subprocess.run(
            ["git", "remote", "get-url", "origin"], cwd=ROOT, capture_output=True, text=True, check=True
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        raise SystemExit("Не вижу git-remote origin — укажи --repo owner/name")
    match = re.search(r"github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?$", url)
    if not match:
        raise SystemExit(f"Не разобрал адрес origin: {url}. Укажи --repo owner/name")
    return match.group(1)


def pages_manifest_url(repo: str) -> str:
    owner, name = repo.split("/", 1)
    return f"https://{owner.lower()}.github.io/{name}/manifest.json"


def pages_launcher_url(repo: str, version: str) -> str:
    """Адрес сборки в той же папке на сервере, где лежит манифест."""
    owner, name = repo.split("/", 1)
    return f"https://{owner.lower()}.github.io/{name}/launcher/{version}/{ASSET_NAME}"


def _version_key(text: str) -> tuple[int, ...]:
    parts: list[int] = []
    for chunk in text.split("."):
        digits = "".join(ch for ch in chunk if ch.isdigit())
        if not digits:
            break
        parts.append(int(digits))
    return tuple(parts)


def stage_launcher(version: str) -> Path:
    """Кладёт собранный exe в docs/launcher/<версия>/ и удаляет лишние старые сборки."""
    target_dir = LAUNCHER_DIR / version
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / ASSET_NAME
    shutil.copy2(DIST_EXE, target)
    print(f"→ docs/launcher/{version}/{ASSET_NAME}")

    keep = {version}
    others = sorted(
        (item for item in LAUNCHER_DIR.iterdir() if item.is_dir() and item.name != version),
        key=lambda item: _version_key(item.name),
        reverse=True,
    )
    keep.update(item.name for item in others[: max(0, KEEP_BUILDS - 1)])
    for item in LAUNCHER_DIR.iterdir():
        if item.is_dir() and item.name not in keep:
            shutil.rmtree(item, ignore_errors=True)
            print(f"удалена старая сборка: docs/launcher/{item.name}")
    return target


def set_source_constants(repo: str) -> None:
    """Прописывает в config.py адрес манифеста на GitHub Pages и зеркала."""
    url = pages_manifest_url(repo)
    fallbacks = [
        f"https://raw.githubusercontent.com/{repo}/main/docs/manifest.json",
        f"https://cdn.jsdelivr.net/gh/{repo}@main/docs/manifest.json",
    ]
    raw = f"https://raw.githubusercontent.com/{repo}/main/docs/manifest.json"
    raw_sig = f"https://raw.githubusercontent.com/{repo}/main/docs/manifest.json.sig"
    text = CONFIG_PATH.read_text(encoding="utf-8")
    text = re.sub(r'GITHUB_REPO = "[^"]*"', f'GITHUB_REPO = "{repo}"', text, count=1)
    text = re.sub(r'DEFAULT_MANIFEST_URL = "[^"]*"', f'DEFAULT_MANIFEST_URL = "{raw}"', text, count=1)
    text = re.sub(
        r'DEFAULT_MANIFEST_SIG_URL = "[^"]*"', f'DEFAULT_MANIFEST_SIG_URL = "{raw_sig}"', text, count=1
    )
    list_body = "".join(f'    "{item}",\n' for item in fallbacks)
    text = re.sub(
        r"MANIFEST_FALLBACKS: list\[str\] = \[[^\]]*\]",
        "MANIFEST_FALLBACKS: list[str] = [\n" + list_body + "]",
        text,
        count=1,
    )
    CONFIG_PATH.write_text(text, encoding="utf-8")
    print(f"config.py: манифест → {url}")
    for item in fallbacks:
        print(f"           зеркало: {item}")


def sync_site_links(repo: str) -> None:
    page = SITE_DIR / "index.html"
    if not page.exists():
        return
    text = page.read_text(encoding="utf-8")
    updated = re.sub(
        r'var RELEASES_URL = "[^"]*"',
        'var RELEASES_URL = "https://github.com/%s/releases"' % repo,
        text,
        count=1,
    )
    if updated != text:
        page.write_text(updated, encoding="utf-8")
        print(f"docs/index.html: ссылка на релизы → github.com/{repo}/releases")


def build() -> None:
    run([sys.executable, "-m", "PyInstaller", "--noconfirm", "Craftunity_launcher.spec"], cwd=ROOT)
    if not DIST_EXE.exists():
        raise SystemExit(f"Сборка не создала {DIST_EXE}")


def upload(repo: str, version: str, notes: str, allow_empty_notes: bool) -> str:
    tag = f"v{version}"
    url = f"https://github.com/{repo}/releases/download/{tag}/{ASSET_NAME}"
    title = f"Craftunity Launcher {version}"
    body = notes.strip() or title
    if not allow_empty_notes:
        print("Пустые заметки — в релиз уйдёт заголовок. Это нормально для мелкой правки.")
    run(
        [
            "gh",
            "release",
            "create",
            tag,
            str(DIST_EXE),
            "--repo",
            repo,
            "--title",
            title,
            "--notes",
            body,
        ],
        cwd=ROOT,
    )
    return url


def publish_manifest(version: str, url: str, digest: str, notes: str, mandatory: bool, mirror: str = "") -> Path:
    data = manifest.manifest_for_admin()
    block = dict(data.get("launcher") or {})
    block.update(
        {
            "version": version,
            "url": url,
            "sha256": digest,
            "notes": notes.strip(),
            "mandatory": bool(mandatory),
        }
    )
    mirror = mirror.strip()
    block["mirrors"] = [mirror] if mirror and mirror != url else []
    data["launcher"] = block
    if config.HEARTBEAT_URL:
        data["heartbeat_url"] = config.HEARTBEAT_URL
    if mandatory:
        data["min_launcher"] = version
    saved = manifest.save_admin_manifest(data)
    print(f"Манифест подписан: {saved.get('version')} → launcher {version}")
    publish = config.data_dir() / "admin" / "publish"
    SITE_DIR.mkdir(parents=True, exist_ok=True)
    for name in ("manifest.json", "manifest.json.sig"):
        shutil.copy2(publish / name, SITE_DIR / name)
        print(f"→ docs/{name}")
    return SITE_DIR / "manifest.json"


def commit_and_push(version: str, push: bool) -> None:
    files = [
        "craftunity/config.py",
        "docs/index.html",
        "docs/manifest.json",
        "docs/manifest.json.sig",
        "docs/launcher",
        "README.md",
    ]
    existing = [name for name in files if (ROOT / name).exists()]
    run(["git", "add", *existing], cwd=ROOT)
    run(["git", "commit", "-m", f"release: Craftunity Launcher {version}"], cwd=ROOT)
    if push:
        run(["git", "push"], cwd=ROOT)
        print("Запушено — GitHub Pages обновится через минуту")
    else:
        print("Коммит создан, но не запушен. Запусти: git push")


def main() -> int:
    parser = argparse.ArgumentParser(description="Сборка и публикация обновления лаунчера Craftunity")
    parser.add_argument("version", help="Новая версия лаунчера, например 1.1.0")
    parser.add_argument("--notes", default="", help="Что нового — попадёт в релиз и манифест")
    parser.add_argument("--repo", default=None, help="GitHub-репозиторий owner/name")
    parser.add_argument("--mandatory", action="store_true", help="Обновление обязательное: старые версии не запустятся")
    parser.add_argument("--skip-build", action="store_true", help="Не собирать exe, взять готовый из dist")
    parser.add_argument("--no-upload", action="store_true", help="Не заливать в GitHub Releases (ссылка всё равно попадёт в манифест)")
    parser.add_argument("--no-commit", action="store_true", help="Не коммитить docs/ и config.py")
    parser.add_argument("--push", action="store_true", help="Запушить коммит сразу")
    parser.add_argument("--dry-run", action="store_true", help="Только показать план, ничего не менять")
    args = parser.parse_args()

    version = args.version.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("Версия должна быть вида 1.2.3")
    old = current_version()
    if tuple(int(x) for x in version.split(".")) <= tuple(int(x) for x in old.split(".")[:3]):
        raise SystemExit(f"Версия {version} не новее текущей {old}")

    repo = repo_slug(args.repo)
    tag_url = f"https://github.com/{repo}/releases/download/v{version}/{ASSET_NAME}"
    folder_url = pages_launcher_url(repo, version)
    print(f"Выпуск {old} → {version}")
    print(f"Папка сервера: {folder_url}")
    print(f"Зеркало:       {tag_url}")
    print(f"Обязательное: {'да' if args.mandatory else 'нет'}")
    if args.dry_run:
        print("Dry run: ничего не меняю.")
        return 0

    sync_site_links(repo)
    set_source_constants(repo)
    bump_version(version)
    if not args.skip_build:
        build()
    if not DIST_EXE.exists():
        raise SystemExit("Нет dist/Craftunity_launcher.exe — собери или убери --skip-build")
    digest = sha256(DIST_EXE)
    print(f"sha256: {digest}")

    stage_launcher(version)
    mirror = ""
    if not args.no_upload:
        mirror = upload(repo, version, args.notes, allow_empty_notes=True)

    publish_manifest(version, folder_url, digest, args.notes, args.mandatory, mirror)
    if not args.no_commit:
        commit_and_push(version, args.push)
    print("\nГотово. Текущие клиенты получат обновление при следующем запуске.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
