from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from craftunity import config, manifest  # noqa: E402

CONFIG_PATH = ROOT / "craftunity" / "config.py"
CSPROJ = ROOT / "c#launcher" / "CraftunityLauncher.csproj"
APP_DIR = ROOT / "dist" / "CraftunityLauncher_app"
DIST_DIR = ROOT / "dist"
ISS = ROOT / "installer" / "craftunity_csharp.iss"
SITE_DIR = ROOT / "docs"
PAGES_DIR = SITE_DIR / "launcher"
INSTALLER_PREREQ = r"C:\Users\БАЛ-ДАН\AppData\Local\Programs\Inno Setup 6\ISCC.exe"
KEEP_SETUPS = 2


def setup_name(version: str) -> str:
    return f"Craftunity_Setup_{version}.exe"


def setup_path(version: str) -> Path:
    return DIST_DIR / setup_name(version)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for piece in iter(lambda: handle.read(256 * 1024), b""):
            digest.update(piece)
    return digest.hexdigest()


def run(command: list[str], **kwargs) -> None:
    print("$ " + " ".join(command))
    subprocess.run(command, check=True, **kwargs)


def current_version() -> str:
    found = re.search(r'LAUNCHER_VERSION\s*=\s*"([^"]+)"', CONFIG_PATH.read_text(encoding="utf-8"))
    if not found:
        raise SystemExit("не нашёл LAUNCHER_VERSION в craftunity/config.py")
    return found.group(1)


def bump_version(version: str) -> None:
    text = CONFIG_PATH.read_text(encoding="utf-8")
    updated = re.sub(r'(LAUNCHER_VERSION\s*=\s*)"[^"]+"', rf'\g<1>"{version}"', text, count=1)
    if updated == text:
        print(f"config.py уже на версии {version}")
    else:
        CONFIG_PATH.write_text(updated, encoding="utf-8")
        print(f"config.py: LAUNCHER_VERSION -> {version}")


def repo_slug(explicit: str | None) -> str:
    if explicit:
        return explicit
    url = subprocess.run(
        ["git", "remote", "get-url", "origin"], cwd=ROOT, capture_output=True, text=True, check=True
    ).stdout.strip()
    match = re.search(r"github\.com[:/]([^/]+/[^/.]+?)(?:\.git)?$", url)
    if not match:
        raise SystemExit(f"не разобрал адрес origin: {url}")
    return match.group(1)


def build_app() -> None:
    run(
        [
            "dotnet",
            "publish",
            str(CSPROJ),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-o",
            str(APP_DIR),
        ],
        cwd=ROOT,
    )
    stub = APP_DIR / "CraftunityLauncher.pdb"
    if stub.exists():
        stub.unlink()


def build_setup(version: str) -> Path:
    compiler = Path(INSTALLER_PREREQ)
    if not compiler.exists():
        raise SystemExit(f"нет ISCC.exe по пути {compiler}")
    run([str(compiler), f"/DAppVersion={version}", str(ISS)], cwd=ROOT)
    target = setup_path(version)
    if not target.exists():
        raise SystemExit(f"установщик не создан: {target}")
    return target


def stage_setup(version: str, setup: Path) -> str:
    target_dir = PAGES_DIR / version
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / setup.name
    shutil.copy2(setup, target)
    print(f"-> docs/launcher/{version}/{setup.name}")
    for item in PAGES_DIR.iterdir():
        if item.is_dir() and item.name != version:
            shutil.rmtree(item, ignore_errors=True)
            print(f"удалена старая сборка: docs/launcher/{item.name}")
    return f"https://daniprocrut-ui.github.io/craftunity/launcher/{version}/{setup.name}"


def publish_manifest(version: str, url: str, digest: str, notes: str, mandatory: bool, mirror: str) -> None:
    data = json.loads((SITE_DIR / "manifest.json").read_text(encoding="utf-8"))
    block = dict(data.get("launcher") or {})
    block.update(
        {
            "version": version,
            "url": url,
            "sha256": digest,
            "notes": notes.strip(),
            "mandatory": bool(mandatory),
            "mirrors": [mirror] if mirror and mirror != url else [],
        }
    )
    data["launcher"] = block
    if mandatory:
        data["min_launcher"] = version
    data["schema"] = manifest.MANIFEST_SCHEMA
    data["updated_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    config.save_json(config.data_dir() / "admin" / "manifest.json", data)
    body, signature = manifest.sign_manifest(data)
    (SITE_DIR / "manifest.json").write_bytes(body)
    (SITE_DIR / "manifest.json.sig").write_bytes(signature)
    verify = manifest.verify_manifest(body, signature)
    if verify.get("launcher", {}).get("version") != version:
        raise SystemExit("подписанный манифест не совпадает с версией")
    print(f"манифест подписан: launcher -> {version}")


def release_asset_url(repo: str, version: str) -> str:
    return f"https://github.com/{repo}/releases/download/v{version}/{setup_name(version)}"


def upload(repo: str, version: str, setup: Path, notes: str) -> None:
    tag = f"v{version}"
    run(
        [
            "gh",
            "release",
            "create",
            tag,
            str(setup),
            "--repo",
            repo,
            "--title",
            f"Craftunity Launcher {version}",
            "--notes",
            notes.strip() or f"Craftunity Launcher {version}",
        ],
        cwd=ROOT,
    )


def commit_and_push(version: str, push: bool) -> None:
    files = ["craftunity/config.py", "docs/manifest.json", "docs/manifest.json.sig", "docs/launcher", "installer"]
    existing = [name for name in files if (ROOT / name).exists()]
    run(["git", "add", *existing], cwd=ROOT)
    run(["git", "commit", "-m", f"release: Craftunity C# installer {version}"], cwd=ROOT)
    if push:
        run(["git", "push"], cwd=ROOT)


def main() -> int:
    parser = argparse.ArgumentParser(description="Релиз C# установщика Craftunity Launcher")
    parser.add_argument("version", help="версия вида 1.5.21")
    parser.add_argument("--notes", default="")
    parser.add_argument("--repo", default="daniprocrut-ui/craftunity")
    parser.add_argument("--mandatory", action="store_true")
    parser.add_argument("--skip-build", action="store_true", help="не собирать exe")
    parser.add_argument("--skip-setup", action="store_true", help="взять готовый установщик из dist")
    parser.add_argument("--no-upload", action="store_true")
    parser.add_argument("--no-commit", action="store_true")
    parser.add_argument("--push", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    version = args.version.strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise SystemExit("версия должна быть вида 1.5.21")

    repo = repo_slug(args.repo)
    if args.dry_run:
        print(f"C# релиз {version}: exe -> дистрибутив -> установщик -> манифест -> gh release")
        return 0

    bump_version(version)
    if not args.skip_build:
        build_app()
    setup = setup_path(version)
    if not args.skip_setup:
        setup = build_setup(version)
    if not setup.exists():
        raise SystemExit(f"нет установщика {setup}")

    digest = sha256(setup)
    size = setup.stat().st_size
    print(f"установщик: {size} байт, sha256 {digest}")

    mirror = stage_setup(version, setup)
    url = release_asset_url(repo, version)
    if not args.no_upload:
        upload(repo, version, setup, args.notes)
    publish_manifest(version, url, digest, args.notes, args.mandatory, mirror)
    if not args.no_commit:
        commit_and_push(version, args.push)
    print("\nГотово.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
