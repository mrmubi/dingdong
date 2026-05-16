// ============================================================
//  DingDong device configuration
// ============================================================
//  All firmware-tunable settings live here as #defines with safe
//  placeholder values. To override any of them with your own
//  (Wi-Fi credentials, ControlPlan IP, etc.) without polluting
//  git:
//
//    1. Copy secrets.h.example  ->  secrets.h
//    2. Edit secrets.h and #define only the values you want to
//       override.
//    3. secrets.h is listed in the repo .gitignore so it never
//       reaches a commit.
//
//  These macros are resolved by the preprocessor at BUILD time --
//  they are baked into the firmware binary, NOT read from disk at
//  runtime. Changing them requires a rebuild and re-flash.
//
//  If secrets.h is absent the defaults below are used (the build
//  still succeeds; the device just won't reach your Wi-Fi -- the
//  placeholders use RFC 5737 documentation IPs that don't route).
// ============================================================
#pragma once

// ---- Pull in user overrides if present --------------------------------
#if defined(__has_include)
#  if __has_include("secrets.h")
#    include "secrets.h"
#  endif
#endif

// ---- Wi-Fi credentials ------------------------------------------------
#ifndef DINGDONG_WIFI_SSID
#define DINGDONG_WIFI_SSID      "ExampleSSID"
#endif

#ifndef DINGDONG_WIFI_PASSWORD
#define DINGDONG_WIFI_PASSWORD  "ExamplePassword123"
#endif

// ---- ControlPlan PC endpoint ------------------------------------------
//  IP/hostname + port shown in the ControlPlan app UI.
// 192.0.2.x is RFC 5737 TEST-NET-1 -- a guaranteed-unroutable
// placeholder. Override with your real PC's LAN IP in secrets.h.
#ifndef DINGDONG_CONFIG_URL
#define DINGDONG_CONFIG_URL     "http://192.0.2.50:8088/config"
#endif

#ifndef DINGDONG_MENTIONS_URL
#define DINGDONG_MENTIONS_URL   "http://192.0.2.50:8088/mentions"
#endif

// ---- Tunables ---------------------------------------------------------
//  Default poll interval (seconds) if the server doesn't return one.
#ifndef DINGDONG_DEFAULT_POLL_SEC
#define DINGDONG_DEFAULT_POLL_SEC  5
#endif

// ---- Shared-secret auth token ----------------------------------------
//  Sent in the X-DingDong-Token header on every request. Must match the
//  AuthToken shown on ControlPlan's "Security" tab. The placeholder below
//  will never authenticate -- override it in secrets.h.
#ifndef DINGDONG_AUTH_TOKEN
#define DINGDONG_AUTH_TOKEN     "replace-me-with-controlplan-token"
#endif
