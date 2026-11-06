# Fabric build-конфиг для Minecraft 26.2 (Java 25) + FancyMenu 3.9.14 + Konkrete

Собрано и проверено по официальным API (meta.fabricmc.net, maven.fabricmc.net,
api.modrinth.com) и официальной документации Fabric (docs.fabricmc.net, fabricmc.net,
ветка `26.2` репозитория `FabricMC/fabric-example-mod`). Дата проверки данных: 22.10.2026.

---

## 1. Таблица версий (проверено по API)

| Компонент | Версия | Источник / как проверено |
|---|---|---|
| Minecraft | `26.2` | задача |
| Fabric Loader (stable) | **`0.20.1`** | `GET https://meta.fabricmc.net/v2/versions/loader/26.2` → первая запись `"stable": true`, maven `net.fabricmc:fabric-loader:0.20.1` |
| Маппинги | **нет (не нужны)** | `GET /v2/versions/yarn/26.2` → `[]` (пусто). Yarn прекращён: MC 26.1+ не обфусцирован, см. раздел 2 |
| Fabric Loom (plugin id) | `net.fabricmc.fabric-loom` (НЕ `-remap`) | docs.fabricmc.net/develop/loom — для MC 26.1+ нужен новый не-ремаппинговый плагин |
| Fabric Loom (версия) | **`1.19-SNAPSHOT`** (в примере-моде); latest/release в metadata = **`1.19.3`** | `https://maven.fabricmc.net/fabric-loom/fabric-loom.gradle.plugin/maven-metadata.xml` → `<latest>1.19.3</latest>`. Блог Fabric для 26.2 на момент релиза рекомендовал Loom 1.17 / Gradle 9.5.1; пример-мод обновлён до 1.19-SNAPSHOT |
| Fabric API | **`0.161.0+26.2`** | `GET https://api.modrinth.com/v2/project/fabric-api/version?game_versions=["26.2"]` → самая свежая release. Maven: `net.fabricmc.fabric-api:fabric-api:0.161.0+26.2` |
| Gradle (wrapper) | **`9.8.2`** | `fabric-example-mod` ветка `26.2`, `gradle/wrapper/gradle-wrapper.properties` → `gradle-9.8.2-bin.zip` (проверялось и у меня: сборка идёт без сетевого Gradle, wrapper качает сам) |
| Java (release/target) | **`25`** | `options.release = 25`, `JavaVersion.VERSION_25` в примере-моде 26.2; MC 26.1+ требует Java 25+ (официальный пост Fabric для 26.1) |

Releases Loom (stable), подтверждённые в maven-metadata: `1.15.5`, `1.16.3`, `1.17.21`, `1.18.4`, `1.19.3` и множество alpha/SNAPSHOT.

---

## 2. ВАЖНО: маппинги и конфигурации зависимостей сильно изменились

MC 26.1 — первая версия Minecraft без обфускации кода
(https://fabricmc.net/2025/10/31/obfuscation.html, https://www.minecraft.net/en-us/article/removing-obfuscation-in-java-edition).

Следствия (подтверждено docs.fabricmc.net/develop/porting/mappings и /develop/loom):

1. **Yarn-маппинги больше не существуют** для 26.2. В `build.gradle` строки `mappings ...` **нет вообще**.
2. Используется **новый плагин** `net.fabricmc.fabric-loom` (без суффикса `-remap`).
   Старый `fabric-loom` / `net.fabricmc.fabric-loom-remap` — только для 1.21.11 и старше.
3. **Конфигурации `modImplementation`, `modApi`, `modCompileOnly`, `modRuntime` УДАЛЕНЫ.**
   Используются стандартные `implementation` / `compileOnly` / `api` / `runtimeOnly`.
4. Задача `remapJar` заменена на `jar`. Официальная цитата (блог Fabric, 26.1):
   *"Any use of `modImplementation`, `modCompileOnly`, or similar should be switched out for
   the standard `implementation` or `compileOnly`, and any use of `remapJar` should be switched
   out for `jar`."*
5. Minecraft-зависимость объявляется как `minecraft "com.mojang:minecraft:26.2"` (координаты Mojang).

> Ключевой ответ на вопрос задачи №7: синтаксис `modCompileOnly files(...)` / `modImplementation files(...)`
> для 26.1+ НЕВЕРЕН — этих конфигураций больше нет. Правильно: **`compileOnly files('libs/fancymenu_fabric_3.9.14_MC_26.2.jar')`**
> (если FancyMenu нужен только на этапе компиляции) либо `implementation files(...)`, если он должен
> попасть в classpath (jar-in-jar выполняется через `include`). Подробнее в разделе 5.

---

## 3. gradle.properties (воспроизводимо с официального примера 26.2)

```properties
# Done to increase the memory available to gradle.
org.gradle.jvmargs=-Xmx1G
org.gradle.parallel=true
org.gradle.configuration-cache=true

# Fabric Properties
minecraft_version=26.2
loader_version=0.20.1
loom_version=1.19-SNAPSHOT
# Альтернатива: loom_version=1.19.3  (latest stable release)

# Mod Properties
version=1.0.0
group=com.example

# Dependencies
fabric_api_version=0.161.0+26.2
```

---

## 4. build.gradle (на базе официального fabric-example-mod, ветка 26.2)

```gradle
plugins {
	id 'net.fabricmc.fabric-loom' version "${loom_version}"
	id 'maven-publish'
}

repositories {
	// Loom сам добавляет все нужные репозитории Fabric/Mojang/Central.
	// Доп. репозитории добавляйте только под сторонние библиотеки/моды.
}

loom {
	splitEnvironmentSourceSets()

	mods {
		"modid" { // замените на ваш mod id
			sourceSet sourceSets.main
			sourceSet sourceSets.client
		}
	}
}

dependencies {
	minecraft "com.mojang:minecraft:${project.minecraft_version}"
	implementation "net.fabricmc:fabric-loader:${project.loader_version}"

	// Fabric API
	implementation "net.fabricmc.fabric-api:fabric-api:${project.fabric_api_version}"

	// --- FancyMenu + Konkrete как локальные jar (см. раздел 5) ---
	compileOnly files("libs/fancymenu_fabric_3.9.14_MC_26.2.jar")
	compileOnly files("libs/konkrete_fabric_1.11.1_MC_26.2.jar")
	compileOnly files("libs/melody_fabric_1.0.17_MC_26.2.jar")
}

processResources {
	def version = project.version
	inputs.property "version", version

	filesMatching("fabric.mod.json") {
		expand "version": version
	}
}

tasks.withType(JavaCompile).configureEach {
	it.options.release = 25
}

java {
	withSourcesJar()

	sourceCompatibility = JavaVersion.VERSION_25
	targetCompatibility = JavaVersion.VERSION_25
}

jar {
	def projectName = project.name
	inputs.property "projectName", projectName

	from("LICENSE") {
		rename { "${it}_$projectName"}
	}
}

publishing {
	publications {
		create("mavenJava", MavenPublication) {
			from components.java
		}
	}
	repositories {
		// сюда добавляют свои publish-репозитории
	}
}
```

### settings.gradle

```gradle
pluginManagement {
	repositories {
		maven {
			name = 'Fabric'
			url = 'https://maven.fabricmc.net/'
		}
		mavenCentral()
		gradlePluginPortal()
	}
}

rootProject.name = 'modid'
```

---

## 5. Зависимости FancyMenu / Konkrete (проверено по Modrinth API)

FancyMenu 3.9.14 для Fabric/26.2:
- project_id: `Wq5SjeWM` (slug `fancymenu`)
- version_number: **`3.9.14-26.2-fabric`**
- файл: **`fancymenu_fabric_3.9.14_MC_26.2.jar`**
- скачать (CDN Modrinth):
  `https://cdn.modrinth.com/data/Wq5SjeWM/versions/2BXE3l12/fancymenu_fabric_3.9.14_MC_26.2.jar`
- `environment`: client only / server optional
- Обязательные зависимости (поле `dependencies` ответа API):
  - `J81TRJWm` = **Konkrete** (fabric)
  - `CVT4pFB2` = **Melody** (fabric)
  - `P7dR8mSH` = **Fabric API** (fabric)

Konkrete для 26.2 (Fabric):
- version_number: **`1.11.1-26.2-fabric`**, файл `konkrete_fabric_1.11.1_MC_26.2.jar`
- src: `https://cdn.modrinth.com/data/J81TRJWm/versions/Ra1AWDOa/konkrete_fabric_1.11.1_MC_26.2.jar`
- сам требует Fabric API (required)

Melody (нужна FancyMenu для 26.2):
- version_number: **`1.0.17-26.2-fabric`**, файл `melody_fabric_1.0.17_MC_26.2.jar`
- src: `https://cdn.modrinth.com/data/CVT4pFB2/versions/ItVQn1cW/melody_fabric_1.0.17_MC_26.2.jar`

### Maven-репозиторий для FancyMenu/Konkrete

**Не найдено.** Публичного Maven-репозитория FancyMenu/Konkrete/Melody не обнаружил:
- FancyMenu/Konkrete распространяются только как jar на Modrinth/CurseForge (в их POM/gradle используется
  локальная публикация на Modrinth, а не публичный Maven-repo типа maven.fabricmc.net).
- На maven.fabricmc.net их нет (там только artifacts Fabric); проверял корень `/fabric-loom/` и поиск по
  группам `de.keksuccino`/`fancymenu` — там их артефактов нет.

Вывод: подключать эти моды как **локальные jar в `libs/`** через `compileOnly files(...)` (или `implementation files(...)`),
как в разделе 4. Это согласуется с тем, что для 26.1+ нет mod-конфигураций и нет ремаппинга.

### ВАЖНО про FancyMenu runtime

FancyMenu 3.9.14 компилируется/собирается под **свои** версии Fabric API/Konkrete/Melody и использует
внутренние API Fabric Loader. Даже при `compileOnly files(...)`:
- сам jar должен лежать в `libs/` при сборке;
- у конечного пользователя в папке `mods/` должны быть ВСЕ 4 jar (FancyMenu, Konkrete, Melody, Fabric API),
  иначе игра вылетит при загрузке.

---

## 6. Java-версия для MC 26.2

- **MC 26.1+ требует Java 25+** (официально, блог Fabric для 26.1 + независимая таблица совместимости mcreference.com).
- В `build.gradle`: `options.release = 25`, `JavaVersion.VERSION_25`.
- Gradle JVM: должен быть ≥ Java 25. У пользователя `javac` 25.0.2 в PATH — подходит.
- IDE: для корректной работы mixins нужна IntelliJ IDEA **2025.3+** (требование Fabric для 26.1+).

---

## 7. Сборка и wrapper

Gradle отдельно не установлен — используется wrapper:

```powershell
# сгенерировать wrapper один раз (нужен работоспособный javac -g? нет, просто Gradle)
# проще: скопировать gradle/wrapper/*, gradlew, gradlew.bat из fabric-example-mod ветки 26.2,
# обновив distributionUrl до gradle-9.8.2-bin.zip

# собрать клиентский мод
.\gradlew.bat build
# сгенерировать source/get
.\gradlew.bat genSources
# запустить клиент
.\gradlew.bat runClient
```

wrapper distributionUrl (из официального примера 26.2):
`gradle-9.8.2-bin.zip` (`https://services.gradle.org/distributions/gradle-9.8.2-bin.zip`)

Примечание: `loom_version=1.19-SNAPSHOT` в примере означает, что нужен доступ к maven.fabricmc.net
для снапшота; стабильный `1.19.3` тоже существует и подойдёт.

---

## 8. Источники (живые ссылки)

- Fabric Loader для 26.2: `https://meta.fabricmc.net/v2/versions/loader/26.2`
- Yarn для 26.2 (пусто): `https://meta.fabricmc.net/v2/versions/yarn/26.2`
- Loom metadata (latest 1.19.3): `https://maven.fabricmc.net/fabric-loom/fabric-loom.gradle.plugin/maven-metadata.xml`
- Fabric API на Modrinth: `https://api.modrinth.com/v2/project/fabric-api/version?game_versions=["26.2"]`
- FancyMenu на Modrinth: `https://api.modrinth.com/v2/project/fancymenu/version?game_versions=["26.2"]`
- Konkrete на Modrinth: `https://api.modrinth.com/v2/project/J81TRJWm/version?game_versions=["26.2"]&loaders=["fabric"]`
- Melody на Modrinth: `https://api.modrinth.com/v2/project/CVT4pFB2/version?game_versions=["26.2"]&loaders=["fabric"]`
- Официальный пример-мод поверх 26.2: `https://github.com/FabricMC/fabric-example-mod/tree/26.2`
  (build.gradle, gradle.properties, gradle/wrapper/gradle-wrapper.properties)
- Loom docs: `https://docs.fabricmc.net/develop/loom`
- Маппинги: `https://docs.fabricmc.net/develop/porting/mappings`
- Портирование на 26.2: `https://docs.fabricmc.net/develop/porting`
- Блог Fabric для 26.1: `https://fabricmc.net/2026/03/14/261.html` (Java 25, переход на Mojang maps)
- Блог Fabric для 26.2: `https://fabricmc.net/2026/06/15/262.html` (Loom 1.17/Gradle 9.5.1 на дату релиза)
- Упразднение обфускации: `https://fabricmc.net/2025/10/31/obfuscation.html`
- Требование Java 25: `https://mcreference.com/compatibility/26.2`

---

## 9. Открытые вопросы / что не удалось подтвердить на 100%

1. **Точная «рекомендованная» версия Loom для 26.2 на сегодня**: блог на дату релиза писал 1.17 +
   Gradle 9.5.1; пример-мод уже обновлён до `1.19-SNAPSHOT` + Gradle 9.8.2, а maven-metadata выдаёт
   latest/release `1.19.3`. Рекомендую использовать ту пару, что лежит сейчас в ветке 26.2 примера
   (`1.19-SNAPSHOT` / Gradle 9.8.2), либо стабильный `1.19.3`, если снапшоты нежелательны.
2. **Поведение FancyMenu 3.9.14 при `compileOnly`**: сам мод ожидает на runtime свои зависимости.
   Чтобы наш мод компилировался, jar FancyMenu/Konkrete/Melody нужны в libs/ на этапе сборки;
   но для запуска игры у пользователя должны лежать те же 3 jar (или FancyMenu в `include`).
   Точные версии Fabric API/Konkrete/Melody, против которых собран FancyMenu 3.9.14-26.2, в API
   Modrinth не указываются (поле `dependencies` не содержит конкретных version_id — только project_id),
   поэтому связку брал свежую для 26.2.
3. **Vulkan backend** в 26.2: мод, трогающий GUI/рендер, должен идти через Blaze3D, а не через raw OpenGL,
   чтобы работать и на OpenGL, и на Vulkan (объявлено в блоге Fabric 26.2).