from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

_PROBE = r'''
$roots = @(
  "${env:ProgramFiles(x86)}\Microsoft\EdgeWebView\Application",
  "${env:ProgramFiles}\Microsoft\EdgeWebView\Application"
)
foreach ($r in $roots) {
  if (Test-Path $r) {
    $exe = Join-Path $r "msedgewebview2.exe"
    if (Test-Path $exe) { Write-Output $exe; exit 0 }
  }
}
exit 1
'''


def webview_exe() -> Path | None:
    """msedgewebview2.exe, если рантайм WebView2 установлен и не заблокирован политикой."""
    if os.name != "nt":
        return None
    for root in (
        os.environ.get("ProgramFiles(x86)"),
        os.environ.get("ProgramFiles"),
    ):
        if not root:
            continue
        base = Path(root) / "Microsoft" / "EdgeWebView" / "Application"
        if not base.is_dir():
            continue
        try:
            candidates = sorted(base.iterdir(), reverse=True)
        except OSError:
            continue
        for folder in candidates:
            exe = folder / "msedgewebview2.exe"
            if exe.is_file():
                return exe
    return None


def blocked_by_policy() -> bool:
    """Проверяет групповые политики блокировки встраивания WebView2."""
    if os.name != "nt":
        return False
    import winreg

    locations = (
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Policies\Microsoft\Edge\WebView2"),
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Policies\Microsoft\Edge\WebView2"),
    )
    try:
        for hive, path in locations:
            try:
                with winreg.OpenKey(hive, path) as key:
                    for name in ("EmbeddingAllowed", "BrowserExecutableFolder"):
                        try:
                            value, _kind = winreg.QueryValueEx(key, name)
                        except OSError:
                            continue
                        if name == "EmbeddingAllowed" and int(value) == 0:
                            return True
            except OSError:
                continue
    except Exception:
        return False
    return False


def available() -> bool:
    """Окно WebView2 можно открыть: рантайм есть и встраивание не запрещено."""
    if os.name != "nt":
        return False
    if blocked_by_policy():
        return False
    return webview_exe() is not None


def run(url: str) -> bool:
    """Открывает окно tkinter + WebView2; False — если окно создать нельзя."""
    try:
        import tkinter as tk
        from tkinter import messagebox
    except Exception:
        return False
    try:
        import webview  # type: ignore
    except Exception:
        return False
    try:
        window = webview.create_window("Craftunity", url, width=1180, height=760, min_size=(880, 560))
        webview.start(gui="edgechromium")
        return window is not None
    except Exception:
        return False
