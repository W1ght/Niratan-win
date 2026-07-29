/*
 * Copyright (C) 2026 Niratan contributors
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */
package mextensionserver.controller;

import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.node.ObjectNode;
import com.fasterxml.jackson.module.kotlin.ExtensionsKt;
import eu.kanade.tachiyomi.animesource.AnimeSource;
import eu.kanade.tachiyomi.animesource.online.AnimeHttpSource;
import eu.kanade.tachiyomi.network.HttpException;
import eu.kanade.tachiyomi.source.Source;
import eu.kanade.tachiyomi.source.online.HttpSource;
import fi.iki.elonen.NanoHTTPD;
import mextensionserver.impl.MExtensionServerLoader;
import mextensionserver.impl.MihonInvoker;
import mextensionserver.model.DataBody;
import okhttp3.Cookie;
import okhttp3.HttpUrl;

import java.lang.reflect.Method;
import java.net.URI;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Niratan protocol overlay for M-Extension-Server 1.0.4.
 *
 * <p>The upstream handler always passes the first source exposed by an APK to
 * {@code MihonInvoker}. Niratan sends the repository source identity as the
 * string-valued {@code sourceId} field. This handler removes that extension
 * field before deserializing the upstream request model, selects the matching
 * Mihon source, and gives the existing invoker a one-source view.</p>
 */
public final class DalvikHandler {
    private final ObjectMapper objectMapper = ExtensionsKt.jacksonObjectMapper();

    public NanoHTTPD.Response serve(NanoHTTPD.IHTTPSession session) {
        try {
            Map<String, String> body = new LinkedHashMap<>();
            session.parseBody(body);
            String json = body.get("postData");
            if (json == null) {
                throw new IllegalArgumentException("No JSON body");
            }

            JsonNode requestNode = objectMapper.readTree(json);
            if (!(requestNode instanceof ObjectNode objectNode)) {
                throw new IllegalArgumentException("JSON body must be an object");
            }
            JsonNode requestedSourceNode = objectNode.remove("sourceId");
            String requestedSourceId =
                requestedSourceNode == null || requestedSourceNode.isNull()
                    ? null
                    : requestedSourceNode.asText();
            DataBody dataBody = objectMapper.treeToValue(
                objectNode,
                DataBody.class);

            MExtensionServerLoader.LoadedExtension loaded =
                MExtensionServerLoader.INSTANCE.loadExtensionFromBase64(
                    dataBody.getData());
            MExtensionServerLoader.LoadedExtension selected =
                selectSource(loaded, requestedSourceId);
            Object source = selected.getSources().getFirst();

            String domain = getDomain(source);
            List<Cookie> cookies = parseCookies(session, domain);
            Object network = getNetwork(source);
            if (!cookies.isEmpty() && network != null) {
                addCookies(network, domain, cookies);
            }

            String userAgent = firstHeader(
                session,
                "user-agent",
                "User-Agent");
            if (userAgent != null && network != null) {
                setUserAgent(network, userAgent);
            }

            Object result = MihonInvoker.INSTANCE.invokeMethod(
                selected,
                dataBody);
            return NanoHTTPD.newFixedLengthResponse(
                NanoHTTPD.Response.Status.OK,
                "application/json",
                objectMapper.writeValueAsString(result));
        } catch (Throwable exception) {
            return errorResponse(exception);
        }
    }

    private static MExtensionServerLoader.LoadedExtension selectSource(
        MExtensionServerLoader.LoadedExtension loaded,
        String requestedSourceId) {
        if (loaded.getSources().isEmpty()) {
            throw new IllegalArgumentException(
                "No sources found in extension");
        }
        if (requestedSourceId == null || requestedSourceId.isBlank()) {
            return loaded;
        }

        final long requested;
        try {
            requested = Long.parseLong(requestedSourceId);
        } catch (NumberFormatException exception) {
            throw new IllegalArgumentException(
                "Invalid sourceId: " + requestedSourceId,
                exception);
        }

        Object match = loaded.getSources().stream()
            .filter(source -> sourceId(source) == requested)
            .findFirst()
            .orElseThrow(() -> new IllegalArgumentException(
                "Source not found in extension: " + requestedSourceId));
        return new MExtensionServerLoader.LoadedExtension(
            List.of(match),
            loaded.getPackageInfo(),
            loaded.getJarFile());
    }

    private static long sourceId(Object source) {
        if (source instanceof Source mangaSource) {
            return mangaSource.getId();
        }
        if (source instanceof AnimeSource animeSource) {
            return animeSource.getId();
        }
        throw new IllegalArgumentException(
            "Unsupported extension source type: " +
                source.getClass().getName());
    }

    private static String getDomain(Object source) {
        try {
            Method getBaseUrl = source.getClass().getMethod("getBaseUrl");
            String baseUrl = (String) getBaseUrl.invoke(source);
            String host = URI.create(baseUrl).getHost();
            return host == null || host.isBlank() ? "localhost" : host;
        } catch (Exception ignored) {
            return "localhost";
        }
    }

    private static Object getNetwork(Object source) {
        if (source instanceof HttpSource mangaSource) {
            return mangaSource.getNetwork();
        }
        if (source instanceof AnimeHttpSource animeSource) {
            return animeSource.getNetwork();
        }
        return null;
    }

    private static List<Cookie> parseCookies(
        NanoHTTPD.IHTTPSession session,
        String domain) {
        String header = firstHeader(session, "cookie", "Cookie");
        if (header == null || header.isBlank()) {
            return List.of();
        }

        Map<String, Cookie> cookies = new LinkedHashMap<>();
        for (String cookieText : header.split(";")) {
            String[] parts = cookieText.trim().split("=", 2);
            if (parts.length != 2 || parts[0].isBlank()) {
                continue;
            }
            Cookie cookie = new Cookie.Builder()
                .name(parts[0].trim())
                .value(parts[1].trim())
                .domain(domain.replaceFirst("^\\.", ""))
                .path("/")
                .build();
            cookies.putIfAbsent(cookie.name(), cookie);
        }
        return new ArrayList<>(cookies.values());
    }

    private static void addCookies(
        Object network,
        String domain,
        List<Cookie> cookies) {
        HttpUrl url = new HttpUrl.Builder()
            .scheme("http")
            .host(domain.replaceFirst("^\\.", ""))
            .build();
        if (network instanceof eu.kanade.tachiyomi.network.NetworkHelper helper) {
            helper.getCookieJar().addAll(url, cookies);
        }
    }

    private static void setUserAgent(Object network, String userAgent) {
        if (network instanceof eu.kanade.tachiyomi.network.NetworkHelper helper) {
            helper.setUA(userAgent);
        }
    }

    private static String firstHeader(
        NanoHTTPD.IHTTPSession session,
        String first,
        String second) {
        String value = session.getHeaders().get(first);
        return value != null ? value : session.getHeaders().get(second);
    }

    private NanoHTTPD.Response errorResponse(Throwable exception) {
        NanoHTTPD.Response.Status status =
            NanoHTTPD.Response.Status.INTERNAL_ERROR;
        int code = 500;
        if (exception instanceof HttpException httpException) {
            code = httpException.getCode();
            status = switch (code) {
                case 400 -> NanoHTTPD.Response.Status.BAD_REQUEST;
                case 401 -> NanoHTTPD.Response.Status.UNAUTHORIZED;
                case 403 -> NanoHTTPD.Response.Status.FORBIDDEN;
                case 404 -> NanoHTTPD.Response.Status.NOT_FOUND;
                default -> NanoHTTPD.Response.Status.INTERNAL_ERROR;
            };
        }
        try {
            return NanoHTTPD.newFixedLengthResponse(
                status,
                "application/json",
                objectMapper.writeValueAsString(Map.of(
                    "error",
                    exception.getMessage() == null
                        ? "Unknown error"
                        : exception.getMessage(),
                    "code",
                    code)));
        } catch (Exception serializationFailure) {
            return NanoHTTPD.newFixedLengthResponse(
                status,
                "application/json",
                "{\"error\":\"Unknown error\",\"code\":500}");
        }
    }
}
