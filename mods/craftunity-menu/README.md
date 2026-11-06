# Craftunity Menu

Мод-дополнение для **FancyMenu**, которое оформляет меню Minecraft в стиле **Craftunity Launcher**:
тёмный фон `#090b14` с оранжевым свечением, скруглённые панели, логотип CRAFTUNITY / LAUNCHER
и кнопки главного меню в цветах лаунчера (`#fb923c` → `#f97316`).

## Что делает мод

При первом запуске игры (или после обновления версии темы) мод раскладывает готовую тему в
`config/fancymenu` игрового профиля и включает кастомизацию главного меню:

| Что ставится | Куда |
|---|---|
| `craftunity_title_screen.txt` (раскладка TitleScreen) | `config/fancymenu/customization/` |
| `background.png`, `logo.png`, `btn_normal.png`, `btn_focus.png` | `config/fancymenu/assets/craftunity/` |
| запись `net.minecraft.client.gui.screens.TitleScreen` | `config/fancymenu/customizablemenus.txt` |
| метка версии темы `.craftunity-theme-installed` | `config/fancymenu/` |

Что появляется в игре:

- фон главного меню из лаунчера (один и тот же на любом разрешении, без растяжения логотипа);
- логотип Craftunity в верхней части меню;
- блок текста о сервере Craftunity 3;
- ванильные кнопки (Одиночная игра, Сетевая игра, Настройки, Realms, Выход) перекрашены
  в стиль лаунчера, выровнены по центру и подписаны стандартными текстами — то есть
  работают как обычно, меняется только внешний вид.

Пользовательские правки не затираются: перезаписываются только файлы темы; если вы изменили
раскладку в редакторе FancyMenu, она сохраняется как есть (повторная установка пропускает
изменённые файлы).

**Перед первым запуском** сделайте копию папки `config/fancymenu` профиля, если уже настраивали
там меню. Вернуть стандартный вид: удалить `config/fancymenu/.craftunity-theme-installed` и
`config/fancymenu/assets/craftunity/`, затем запустить игру — тема встанет заново.

## Требования

- Minecraft **26.2**, Fabric Loader **0.20.1+**, Java **25+**;
- в папке `mods/`: **FancyMenu 3.9.14-26.2-fabric**, **Konkrete 1.11.1-26.2-fabric**,
  **Melody 1.0.17-26.2-fabric**, **Fabric API 0.161.0+26.2** — без них FancyMenu не запустится;
- сам мод — только клиентский, на сервер его ставить не нужно.

## Установка

1. Соберите jar: `gradlew.bat build` → файл `build/libs/craftunity-menu-1.0.0.jar`.
2. Положите jar в папку `mods` профиля вместе с FancyMenu, Konkrete, Melody и Fabric API.
3. Запустите игру один раз — тема установится автоматически, в логе будет строка
   `[Craftunity] Установлена тема меню 1.0.0`.

FancyMenu включает кастомизацию меню только для экранов из `customizablemenus.txt` — мод
дописывает туда `TitleScreen` и не трогает остальные записи.

## Сборка

```powershell
$env:JAVA_HOME='C:\Program Files\Java\jdk-25.0.2'
.\gradlew.bat build
```

Используется официальный шаблон Fabric для 26.2: плагин `net.fabricmc.fabric-loom` **1.19.3**,
Gradle wrapper **9.8.2**, `options.release = 25`, маппингов нет (в 26.x код Minecraft не обфусцирован,
Yarn не требуется, конфигурации `modImplementation`/`remapJar` больше не существуют).

FancyMenu не опубликован в публичном Maven, поэтому как compile-зависимость он не подключён:
мод общается с игрой только через файлы и `fabric.mod.json`, поэтому обновление FancyMenu его не ломает.

## Ассеты

`assets/*.png` генерируются скриптом `tools/generate_assets.py` (Pillow) из палитры `ui/styles.css`
и иконки лаунчера `icon.png`. После правки скрипта:

```powershell
python mods\craftunity-menu\tools\generate_assets.py
Copy-Item mods\craftunity-menu\assets\*.png mods\craftunity-menu\src\main\resources\craftunitymenu\assets\craftunity\
```

## Проверка без игры

```powershell
java -cp "<fabric-loader>;<slf4j>;build\libs\craftunity-menu-1.0.0.jar;<classes>" TestInstall <папка>
```

Симуляция установки проверяет, что файлы попадают по нужным путям, `customizablemenus.txt`
дополняется без потери существующих записей, а повторный запуск ничего не перезаписывает.

## Ограничения

- Тема привязана к `TitleScreen` 26.2. В более старых версиях Minecraft (FancyMenu 3.x для 1.20.1/1.21.x)
  формат раскладки другой: пути к ассетам и набор полей отличаются, такой jar туда не ставится.
- Раскладку дальше удобно доделывать в редакторе FancyMenu (`CTRL + ALT + C`); после правок в игре
  мод не будет перезаписывать вашу версию файла.
- Звуки кнопок, анимации и переключение раскладок по кнопкам в этот пакет не входят.
