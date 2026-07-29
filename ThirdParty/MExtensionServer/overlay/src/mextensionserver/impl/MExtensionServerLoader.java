/*
 * Copyright (C) 2026 Niratan contributors
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
package mextensionserver.impl;

import android.content.pm.PackageInfo;
import android.os.Bundle;
import eu.kanade.tachiyomi.animesource.AnimeSource;
import eu.kanade.tachiyomi.animesource.AnimeSourceFactory;
import eu.kanade.tachiyomi.source.Source;
import eu.kanade.tachiyomi.source.SourceFactory;
import mextensionserver.util.Extension;
import mextensionserver.util.PackageTools;

import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.util.Base64;
import java.util.List;
import java.util.UUID;

/**
 * Niratan compatibility overlay for the upstream extension loader.
 *
 * <p>Recent R8 versions can emit trivial companion-object construction in a
 * DEX form that dex2jar 2.4 translates into {@code new Object()} even though
 * the destination field has the companion type. The JVM rejects that output
 * at runtime. The upstream loading flow is retained, with a narrow bytecode
 * repair between DEX conversion and class loading.</p>
 */
public final class MExtensionServerLoader {
    public static final MExtensionServerLoader INSTANCE =
        new MExtensionServerLoader();

    private static final String MANGA_CLASS =
        "tachiyomi.extension.class";
    private static final String ANIME_CLASS =
        "tachiyomi.animeextension.class";

    private final File tempDir;

    private MExtensionServerLoader() {
        try {
            tempDir = Files.createTempDirectory(
                "mextensionserver").toFile();
        } catch (IOException exception) {
            throw new ExceptionInInitializerError(exception);
        }
        try {
            Runtime.getRuntime().addShutdownHook(
                new Thread(() -> deleteRecursively(tempDir)));
        } catch (IllegalStateException ignored) {
            // Shutdown is already in progress.
        }
    }

    public LoadedExtension loadExtensionFromBase64(String base64Data) {
        byte[] apkData = Base64.getDecoder().decode(base64Data);
        File apkFile = new File(
            tempDir,
            "extension-" + UUID.randomUUID() + ".apk");
        try {
            Files.write(apkFile.toPath(), apkData);
            PackageInfo packageInfo =
                PackageTools.INSTANCE.getPackageInfo(
                    apkFile.getAbsolutePath());
            String className = getExtensionClassName(packageInfo);
            File jarFile = new File(
                tempDir,
                "extension-" + UUID.randomUUID() + ".jar");

            PackageTools.INSTANCE.dex2jar(apkFile, jarFile);
            R8ConstructorFixer.patch(jarFile.toPath());
            Extension.INSTANCE.extractAssetsFromApk(apkFile, jarFile);

            Object main = PackageTools.INSTANCE.loadExtensionSources(
                jarFile,
                className,
                apkFile);
            return new LoadedExtension(
                getSources(main),
                packageInfo,
                jarFile);
        } catch (RuntimeException exception) {
            throw exception;
        } catch (Exception exception) {
            throw new IllegalStateException(
                "Failed to load extension",
                exception);
        } finally {
            try {
                Files.deleteIfExists(apkFile.toPath());
            } catch (IOException ignored) {
                // The private temp directory is retried during cleanup.
            }
        }
    }

    public void cleanupTempFiles() {
        File[] files = tempDir.listFiles();
        if (files == null) {
            return;
        }
        for (File file : files) {
            if (file.isFile()) {
                try {
                    Files.deleteIfExists(file.toPath());
                } catch (IOException ignored) {
                    // Best-effort cleanup of disposable sidecar files.
                }
            }
        }
    }

    private static String getExtensionClassName(
        PackageInfo packageInfo) {
        Bundle metadata = packageInfo.applicationInfo.metaData;
        String suffix = metadata == null
            ? null
            : metadata.getString(MANGA_CLASS);
        if (suffix == null && metadata != null) {
            suffix = metadata.getString(ANIME_CLASS);
        }
        if (suffix == null) {
            throw new IllegalArgumentException(
                "No source class found in extension metadata");
        }
        return suffix.startsWith(".")
            ? packageInfo.packageName + suffix
            : suffix;
    }

    private static List<?> getSources(Object main) {
        if (main instanceof Source source) {
            return List.of(source);
        }
        if (main instanceof SourceFactory factory) {
            return factory.createSources();
        }
        if (main instanceof AnimeSource source) {
            return List.of(source);
        }
        if (main instanceof AnimeSourceFactory factory) {
            return factory.createSources();
        }
        throw new IllegalArgumentException(
            "Unknown source class type: " +
                main.getClass().getName());
    }

    private static void deleteRecursively(File file) {
        File[] children = file.listFiles();
        if (children != null) {
            for (File child : children) {
                deleteRecursively(child);
            }
        }
        try {
            Files.deleteIfExists(file.toPath());
        } catch (IOException ignored) {
            // Process shutdown cleanup is best effort.
        }
    }

    public static final class LoadedExtension {
        private final List<?> sources;
        private final PackageInfo packageInfo;
        private final File jarFile;

        public LoadedExtension(
            List<?> sources,
            PackageInfo packageInfo,
            File jarFile) {
            this.sources = sources;
            this.packageInfo = packageInfo;
            this.jarFile = jarFile;
        }

        public List<?> getSources() {
            return sources;
        }

        public PackageInfo getPackageInfo() {
            return packageInfo;
        }

        public File getJarFile() {
            return jarFile;
        }
    }
}
