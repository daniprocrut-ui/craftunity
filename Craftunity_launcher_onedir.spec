# -*- mode: python ; coding: utf-8 -*-
# Сборка «без распаковки»: папка вместо одного exe.
# Не использует %TEMP%, поэтому антивирус не может удалить python-библиотеки
# из временной папки при запуске (ошибка Failed to load Python DLL).
from PyInstaller.utils.hooks import collect_data_files
from PyInstaller.utils.hooks import collect_submodules
from PyInstaller.utils.hooks import collect_all

datas = [('ui', 'ui'), ('keys', 'keys')]
binaries = []
hiddenimports = ['webview', 'clr', 'encodings.idna']
datas += collect_data_files('certifi')
hiddenimports += collect_submodules('pythonnet')
tmp_ret = collect_all('webview')
datas += tmp_ret[0]; binaries += tmp_ret[1]; hiddenimports += tmp_ret[2]


a = Analysis(
    ['run.py'],
    pathex=[],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='Craftunity_launcher',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['icon.ico'],
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='Craftunity_launcher',
)
