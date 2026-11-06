package com.craftunity.menu;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.List;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.loader.api.FabricLoader;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * Копирует тему в config/fancymenu при первом запуске (или после обновления мода).
 * Пользовательские файлы не затираются: перезаписываются только файлы темы,
 * отмеченные версией в служебном файле.
 */
public final class CraftunityMenuClient implements ClientModInitializer {

    public static final String MOD_ID = "craftunity_menu";
    public static final String THEME_VERSION = "1.0.0";

    private static final Logger LOGGER = LoggerFactory.getLogger("CraftunityMenu");
    private static final String RESOURCE_ROOT = "craftunitymenu/";
    private static final String STAMP_FILE = ".craftunity-theme-installed";
    private static final String TITLE_SCREEN_CLASS = "net.minecraft.client.gui.screens.TitleScreen";

    @Override
    public void onInitializeClient() {
        try {
            Path configDir = FabricLoader.getInstance().getConfigDir();
            install(configDir.resolve("fancymenu"));
        } catch (Throwable error) {
            LOGGER.warn("[Craftunity] Не удалось установить тему меню: {}", error.toString());
        }
    }

    private void install(Path fancyMenu) throws IOException {
        Path stamp = fancyMenu.resolve(STAMP_FILE);
        if (Files.isRegularFile(stamp) && THEME_VERSION.equals(Files.readString(stamp, StandardCharsets.UTF_8).trim())) {
            LOGGER.info("[Craftunity] Тема меню уже установлена ({})", THEME_VERSION);
            return;
        }

        List<String> index = readIndex();
        if (index.isEmpty()) {
            LOGGER.error("[Craftunity] В jar нет списка файлов темы ({})", RESOURCE_ROOT + "index.txt");
            return;
        }

        int copied = 0;
        for (String entry : index) {
            if (isSafeRelative(entry) && copyResource(RESOURCE_ROOT + entry, fancyMenu.resolve(entry))) {
                copied++;
            }
        }
        enableTitleScreenCustomization(fancyMenu.resolve("customizablemenus.txt"));

        Files.writeString(stamp, THEME_VERSION, StandardCharsets.UTF_8);
        LOGGER.info("[Craftunity] Установлена тема меню {} ({} файлов)", THEME_VERSION, copied);
    }

    private List<String> readIndex() throws IOException {
        List<String> entries = new ArrayList<>();
        try (InputStream stream = getClass().getClassLoader().getResourceAsStream(RESOURCE_ROOT + "index.txt")) {
            if (stream == null) {
                return entries;
            }
            for (String line : new String(stream.readAllBytes(), StandardCharsets.UTF_8).split("\\R")) {
                String entry = line.strip();
                if (!entry.isEmpty() && !entry.startsWith("#")) {
                    entries.add(entry);
                }
            }
        }
        return entries;
    }

    /**
     * FancyMenu включает кастомизацию экрана только если экран перечислен в
     * customizablemenus.txt. Добавляем TitleScreen, не трогая остальные записи.
     */
    private void enableTitleScreenCustomization(Path file) throws IOException {
        String text = Files.isRegularFile(file) ? Files.readString(file, StandardCharsets.UTF_8) : "type = customizablemenus\n";
        if (text.contains(TITLE_SCREEN_CLASS)) {
            return;
        }
        StringBuilder builder = new StringBuilder(text);
        if (!builder.isEmpty() && builder.charAt(builder.length() - 1) != '\n') {
            builder.append('\n');
        }
        builder.append('\n').append(TITLE_SCREEN_CLASS).append(" {\n}\n");
        Files.createDirectories(file.getParent());
        Files.writeString(file, builder.toString(), StandardCharsets.UTF_8);
        LOGGER.info("[Craftunity] Кастомизация главного меню включена в {}", file);
    }

    private static boolean isSafeRelative(String entry) {
        return !entry.isEmpty() && !entry.contains("..") && !Path.of(entry).isAbsolute();
    }

    private boolean copyResource(String resource, Path target) throws IOException {
        try (InputStream stream = getClass().getClassLoader().getResourceAsStream(resource)) {
            if (stream == null) {
                LOGGER.warn("[Craftunity] В jar нет ресурса {}", resource);
                return false;
            }
            Files.createDirectories(target.getParent());
            if (Files.exists(target) && !isManagedThemeFile(target)) {
                LOGGER.info("[Craftunity] Пропускаю {} — файл изменён вручную", target.getFileName());
                return false;
            }
            Files.copy(stream, target, StandardCopyOption.REPLACE_EXISTING);
            return true;
        }
    }

    private boolean isManagedThemeFile(Path target) {
        String normalized = target.toString().replace('\\', '/');
        if (normalized.contains("/fancymenu/assets/craftunity/")) {
            return true;
        }
        if (normalized.endsWith(".txt")) {
            try {
                String head = Files.readString(target, StandardCharsets.UTF_8);
                return head.contains("type = fancymenu_layout");
            } catch (IOException ignored) {
                return false;
            }
        }
        return false;
    }
}
