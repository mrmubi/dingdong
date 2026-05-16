// ============================================================
//  DingDong firmware for MXChip AZ3166 IoT DevKit
//
//  Polls a ControlPlan PC app over HTTP for the list of enabled
//  utilities, then cycles through them with button A (next) /
//  button B (previous). Utility IDs match ControlPlan/AppSettings.cs.
// ============================================================
#include <Arduino.h>
#include <time.h>
#include "AZ3166WiFi.h"
#include "OledDisplay.h"
#include "RGB_LED.h"
#include "http_client.h"
#include "SystemTickCounter.h"
#include "SystemTime.h"
#include "Sensor.h"
#include "config.h"

// ---- Globals ----------------------------------------------------------------
static DevI2C *gI2C = nullptr;
static HTS221Sensor *gTempHum = nullptr;
static LPS22HBSensor *gPressure = nullptr;
static LIS2MDLSensor *gMag = nullptr;
static LSM6DSLSensor *gAccGyro = nullptr;
static RGB_LED gRgb;

static char gStatusLine[32] = "boot";
static int  gEnabled[24];    // utility IDs the user enabled (server-supplied)
static int  gEnabledCount = 0;
static int  gCurrentIdx = 0; // index into gEnabled
static uint64_t gLastSwitchMs = 0;
static int  gPrevA = HIGH, gPrevB = HIGH;
static uint64_t gLastConfigSyncMs = 0;
// How often to cycle WiFi up and re-fetch /config. The AZ3166 BSP has a
// delayed-reset bug ~20 s after WiFi.begin(), so we MUST disconnect well
// inside that window. 60 s between cycles keeps the device idle ~90% of the
// time while still feeling "live" for @Mentions updates.
static const uint32_t kConfigSyncIntervalMs = 60000;
static const uint32_t kWifiConnectTimeoutMs = 8000;

// WebDash (utility 20) — server runs at boot, accessible from any gadget.
static WiFiServer *gWebServer = nullptr;
static int  gWebHits = 0;
static char gWebIp[24] = "-";

// Mentions (utility 21) — values populated from /config response in setup().
static int  gMentionsChats = -1;     // -1 = unknown / not fetched yet
static int  gMentionsEmails = -1;
static bool gMentionsTrackChats = true;
static bool gMentionsTrackEmails = true;

// ---- Utility framework ------------------------------------------------------
typedef void (*UtilFn)();
struct Utility {
    int   id;
    const char *shortName;
    UtilFn onEnter;   // called once when becoming active (clear screen, init state)
    UtilFn onTick;    // called repeatedly (~every 50 ms)
};

// Forward declarations of every utility's handlers (defined below).
static void util_blank_enter();          static void util_blank_tick();
static void util_pomodoro_enter();       static void util_pomodoro_tick();
static void util_comfort_enter();        static void util_comfort_tick();
static void util_wifi_enter();           static void util_wifi_tick();
static void util_clock_enter();          static void util_clock_tick();
static void util_steps_enter();          static void util_steps_tick();
static void util_level_enter();          static void util_level_tick();
static void util_compass_enter();        static void util_compass_tick();
static void util_noise_enter();          static void util_noise_tick();
static void util_webdash_enter();        static void util_webdash_tick();
static void util_mentions_enter();       static void util_mentions_tick();
static void util_stub_enter();           static void util_stub_tick();

// Catalogue — order/IDs MUST match ControlPlan/AppSettings.cs.
static Utility gUtilities[] = {
    { 1,  "Pomodoro",  util_pomodoro_enter, util_pomodoro_tick },
    { 2,  "StandUp",   util_stub_enter,     util_stub_tick     },
    { 3,  "DND",       util_stub_enter,     util_stub_tick     },
    { 4,  "Chess",     util_stub_enter,     util_stub_tick     },
    { 5,  "Comfort",   util_comfort_enter,  util_comfort_tick  },
    { 6,  "PresAlert", util_stub_enter,     util_stub_tick     },
    { 7,  "SleepLog",  util_stub_enter,     util_stub_tick     },
    { 8,  "CILight",   util_stub_enter,     util_stub_tick     },
    { 9,  "PingMon",   util_stub_enter,     util_stub_tick     },
    {10,  "WiFiScan",  util_wifi_enter,     util_wifi_tick     },
    {11,  "Clock",     util_clock_enter,    util_clock_tick    },
    {12,  "Steps",     util_steps_enter,    util_steps_tick    },
    {13,  "Level",     util_level_enter,    util_level_tick    },
    {14,  "Compass",   util_compass_enter,  util_compass_tick  },
    {15,  "Macro",     util_stub_enter,     util_stub_tick     },
    {16,  "Noise",     util_noise_enter,    util_noise_tick    },
    {17,  "IR",        util_stub_enter,     util_stub_tick     },
    {18,  "AzureIoT",  util_stub_enter,     util_stub_tick     },
    {19,  "MQTT",      util_stub_enter,     util_stub_tick     },
    {20,  "WebDash",   util_stub_enter,     util_stub_tick     },
    {21,  "Mentions",  util_mentions_enter, util_mentions_tick },
};
static const int kUtilCount = sizeof(gUtilities)/sizeof(gUtilities[0]);

static const Utility *findUtilityById(int id) {
    for (int i = 0; i < kUtilCount; i++) if (gUtilities[i].id == id) return &gUtilities[i];
    return nullptr;
}

// ============================================================================
//  Setup & main loop
// ============================================================================
void setup() {
    Serial.begin(115200);
    Screen.init();
    Screen.clean();
    Screen.print(0, "DingDong booting");

    pinMode(USER_BUTTON_A, INPUT);
    pinMode(USER_BUTTON_B, INPUT);

    gRgb.turnOff();

    // Bring up I2C and sensors (failures are non-fatal — utility will show error).
    gI2C = new DevI2C(D14, D15);
    gTempHum = new HTS221Sensor(*gI2C);    gTempHum->init(NULL); gTempHum->enable();
    gPressure = new LPS22HBSensor(*gI2C);  gPressure->init(NULL);
    gMag = new LIS2MDLSensor(*gI2C);       gMag->init(NULL);
    gAccGyro = new LSM6DSLSensor(*gI2C, D4, D5);
    gAccGyro->init(NULL);
    gAccGyro->enableAccelerator();
    gAccGyro->enableGyroscope();

    Screen.print(1, "Connect WiFi...");
    Serial.print("WiFi SSID: "); Serial.println(DINGDONG_WIFI_SSID);
    WiFi.begin(DINGDONG_WIFI_SSID, DINGDONG_WIFI_PASSWORD);
    int waitMs = 0;
    while (WiFi.status() != WL_CONNECTED && waitMs < 20000) {
        delay(500); waitMs += 500;
        Serial.print(".");
    }
    Serial.println();
    if (WiFi.status() == WL_CONNECTED) {
        IPAddress ip = WiFi.localIP();
        char buf[24];
        snprintf(buf, sizeof(buf), "%u.%u.%u.%u", ip[0], ip[1], ip[2], ip[3]);
        Screen.print(1, buf);
        Screen.print(2, "Sync NTP...");
        SyncTime();
        Screen.print(2, "Waiting config");
        snprintf(gStatusLine, sizeof(gStatusLine), "%s", buf);
    } else {
        Screen.print(1, "WiFi FAIL");
        Screen.print(2, "Check secrets.h");
        snprintf(gStatusLine, sizeof(gStatusLine), "no wifi");
    }

    // Fallback gadget list when ControlPlan is unreachable (no WiFi, server
    // down, auth failure, etc).  We enable every gadget with a real handler
    // so the user still has a useful menu to cycle through with A/B until
    // the device can reach the server.  IDs that map to util_stub_enter are
    // intentionally omitted -- they would just show "stub" on the OLED.
    // A successful pollConfig() below overwrites this list with the user's
    // selection from the PC.
    static const int kFallbackEnabled[] = {
        1,   // Pomodoro
        5,   // Comfort (temp/humidity)
        10,  // WiFi survey
        11,  // Clock
        12,  // Steps
        13,  // Level
        14,  // Compass
        16,  // Noise meter
        21,  // Mentions (renders cached counts; shows "?" until first sync)
    };
    gEnabledCount = sizeof(kFallbackEnabled) / sizeof(kFallbackEnabled[0]);
    for (int i = 0; i < gEnabledCount; i++) gEnabled[i] = kFallbackEnabled[i];
    gCurrentIdx = 0;

    // One-shot config sync from ControlPlan (no further polling after this).
    // /config also returns the current Mentions counts, so a single round
    // trip populates everything.  We deliberately do not make a second HTTP
    // call here -- it destabilises the AZ3166 networking stack.
    if (WiFi.status() == WL_CONNECTED) {
        Serial.println("Fetching config (one-shot)...");
        pollConfig();
        // After our one-shot fetch, drop WiFi entirely.  The AZ3166 BSP has
        // a delayed-reset bug ~20 s after a connected WiFi link comes up;
        // disconnecting once we have what we need keeps the device stable.
        Serial.println("Disconnecting WiFi (one-shot complete)...");
        WiFi.disconnect();
    }

    gLastConfigSyncMs = SystemTickCounterRead();

    Screen.clean();
    delay(100); // small settling pause after the HTTP socket fully closes
    const Utility *u = findUtilityById(gEnabled[gCurrentIdx]);
    if (u && u->onEnter) u->onEnter();
    Serial.print("setup: active id="); Serial.print(gEnabled[gCurrentIdx]);
    Serial.println(" — entering loop");
}

static void switchTo(int newIdx) {
    if (gEnabledCount == 0) return;
    newIdx = ((newIdx % gEnabledCount) + gEnabledCount) % gEnabledCount;
    if (newIdx == gCurrentIdx) return;
    gCurrentIdx = newIdx;
    Screen.clean();
    const Utility *u = findUtilityById(gEnabled[gCurrentIdx]);
    if (u && u->onEnter) u->onEnter();
}

static void handleButtons() {
    int a = digitalRead(USER_BUTTON_A);
    int b = digitalRead(USER_BUTTON_B);
    uint64_t now = SystemTickCounterRead();
    bool debounced = (now - gLastSwitchMs) > 200;
    if (a == LOW && gPrevA == HIGH && debounced) { switchTo(gCurrentIdx + 1); gLastSwitchMs = now; }
    if (b == LOW && gPrevB == HIGH && debounced) { switchTo(gCurrentIdx - 1); gLastSwitchMs = now; }
    gPrevA = a; gPrevB = b;
}

// --- Tiny JSON helper: find an integer array under a top-level key. ---------
//  Parses {"pollSec":5,"enabled":[1,5,11,13]}
static void parseConfigJson(const char *body) {
    if (!body) return;
    // enabled
    const char *p = strstr(body, "\"enabled\"");
    if (!p) return;
    p = strchr(p, '[');
    if (!p) return;
    int count = 0;
    p++;
    while (*p && *p != ']' && count < (int)(sizeof(gEnabled)/sizeof(gEnabled[0]))) {
        while (*p == ' ' || *p == ',' || *p == '\t' || *p == '\n' || *p == '\r') p++;
        if (*p == ']' || !*p) break;
        int v = atoi(p);
        if (v > 0 && findUtilityById(v)) gEnabled[count++] = v;
        while (*p && *p != ',' && *p != ']') p++;
    }
    if (count > 0) {
        gEnabledCount = count;
        gCurrentIdx = 0;
    }

    // Mentions counters (optional fields in the same payload).
    const char *m;
    m = strstr(body, "\"chats\"");   if (m) { m = strchr(m, ':'); if (m) gMentionsChats  = atoi(m + 1); }
    m = strstr(body, "\"emails\"");  if (m) { m = strchr(m, ':'); if (m) gMentionsEmails = atoi(m + 1); }
    m = strstr(body, "\"trackChats\"");  if (m) { m = strchr(m, ':'); if (m) gMentionsTrackChats  = (strncmp(m + 1, " true", 5) == 0 || strncmp(m + 1, "true", 4) == 0); }
    m = strstr(body, "\"trackEmails\""); if (m) { m = strchr(m, ':'); if (m) gMentionsTrackEmails = (strncmp(m + 1, " true", 5) == 0 || strncmp(m + 1, "true", 4) == 0); }
}

static void pollConfig() {
    if (WiFi.status() != WL_CONNECTED) return;
    HTTPClient client(HTTP_GET, DINGDONG_CONFIG_URL);
    // Authenticate to ControlPlan with the shared-secret token.
    client.set_header("X-DingDong-Token", DINGDONG_AUTH_TOKEN);
    const Http_Response *resp = client.send();
    if (resp && resp->status_code == 200 && resp->body) {
        // body may not be null-terminated; copy to a buffer
        int n = resp->body_length;
        if (n > 511) n = 511;
        char buf[512];
        memcpy(buf, resp->body, n);
        buf[n] = '\0';
        parseConfigJson(buf);
        Serial.print("config OK ("); Serial.print(gEnabledCount); Serial.println(" enabled)");
    } else {
        Serial.print("config fetch failed status=");
        Serial.println(resp ? resp->status_code : -1);
    }
}

// Periodic WiFi cycle: bring the radio up briefly, fetch /config, drop it
// again before the AZ3166 BSP delayed-reset bug kicks in (~20 s window).
static void refreshConfigCycle() {
    Serial.println("refresh: WiFi.begin()...");
    WiFi.begin(DINGDONG_WIFI_SSID, DINGDONG_WIFI_PASSWORD);
    uint32_t waited = 0;
    while (WiFi.status() != WL_CONNECTED && waited < kWifiConnectTimeoutMs) {
        delay(250);
        waited += 250;
    }
    if (WiFi.status() == WL_CONNECTED) {
        pollConfig();
    } else {
        Serial.println("refresh: WiFi connect timed out");
    }
    WiFi.disconnect();
    Serial.println("refresh: WiFi disconnected");
}

void loop() {
    handleButtons();

    // Periodic config re-sync: brief WiFi up/down to dodge BSP reboot bug.
    uint64_t now = SystemTickCounterRead();
    if ((uint32_t)(now - gLastConfigSyncMs) >= kConfigSyncIntervalMs) {
        gLastConfigSyncMs = now;
        refreshConfigCycle();
    }

    if (gEnabledCount > 0) {
        const Utility *u = findUtilityById(gEnabled[gCurrentIdx]);
        if (u && u->onTick) u->onTick();
    } else {
        Screen.print(0, "No utilities");
        Screen.print(1, "Open Control");
        Screen.print(2, "Plan app, tick");
        Screen.print(3, "items, start srv");
    }

    delay(50);
}

// ============================================================================
//  Utility implementations
// ============================================================================

// --- Header helper: shows utility name + nav hint on lines 0/3 --------------
static void drawHeader(const char *name) {
    char l0[24];
    snprintf(l0, sizeof(l0), "%d/%d %s", gCurrentIdx + 1, gEnabledCount, name);
    Screen.print(0, l0);
}

// --- 1. Pomodoro -------------------------------------------------------------
static uint64_t gPomoStart = 0;
static bool     gPomoBreak = false;
static void util_pomodoro_enter() {
    gPomoStart = SystemTickCounterRead();
    gPomoBreak = false;
    drawHeader("Pomodoro");
}
static void util_pomodoro_tick() {
    uint64_t now = SystemTickCounterRead();
    uint32_t elapsedSec = (uint32_t)((now - gPomoStart) / 1000);
    uint32_t total = gPomoBreak ? 5 * 60 : 25 * 60;
    if (elapsedSec >= total) {
        gPomoBreak = !gPomoBreak;
        gPomoStart = now;
        elapsedSec = 0;
        if (gPomoBreak) gRgb.setColor(0, 255, 0); else gRgb.setColor(255, 0, 0);
    }
    uint32_t left = total - elapsedSec;
    char l[20];
    snprintf(l, sizeof(l), "%s", gPomoBreak ? "BREAK" : "FOCUS");
    Screen.print(1, l);
    snprintf(l, sizeof(l), "%02u:%02u", (unsigned)(left / 60), (unsigned)(left % 60));
    Screen.print(2, l);
    Screen.print(3, "A=next B=prev");
}

// --- 5. Room comfort monitor -------------------------------------------------
static void util_comfort_enter() { drawHeader("Comfort"); }
static void util_comfort_tick() {
    static uint64_t last = 0;
    uint64_t now = SystemTickCounterRead();
    if (now - last < 1000) return;
    last = now;
    float temp = 0, hum = 0, pres = 0;
    if (gTempHum) { gTempHum->getTemperature(&temp); gTempHum->getHumidity(&hum); }
    if (gPressure) { gPressure->getPressure(&pres); }
    char l[24];
    snprintf(l, sizeof(l), "T %.1fC H %.0f%%", temp, hum);
    Screen.print(1, l);
    snprintf(l, sizeof(l), "P %.1f hPa", pres);
    Screen.print(2, l);
    Screen.print(3, gStatusLine);
}

// --- 10. WiFi surveyor -------------------------------------------------------
static void util_wifi_enter() { drawHeader("WiFi"); }
static void util_wifi_tick() {
    static uint64_t last = 0;
    uint64_t now = SystemTickCounterRead();
    if (now - last < 1000) return;
    last = now;
    char l[24];
    snprintf(l, sizeof(l), "SSID:%s", WiFi.SSID());
    Screen.print(1, l);
    snprintf(l, sizeof(l), "RSSI:%d dBm", (int)WiFi.RSSI());
    Screen.print(2, l);
    IPAddress ip = WiFi.localIP();
    snprintf(l, sizeof(l), "%u.%u.%u.%u", ip[0], ip[1], ip[2], ip[3]);
    Screen.print(3, l);
}

// --- 11. NTP clock -----------------------------------------------------------
static void util_clock_enter() { drawHeader("Clock"); }
static void util_clock_tick() {
    static uint64_t last = 0;
    uint64_t now = SystemTickCounterRead();
    if (now - last < 500) return;
    last = now;
    time_t t = time(NULL);
    struct tm *lt = localtime(&t);
    if (!lt) { Screen.print(1, "no time"); return; }
    char l[24];
    strftime(l, sizeof(l), "%H:%M:%S", lt);
    Screen.print(1, l);
    strftime(l, sizeof(l), "%a %d %b", lt);
    Screen.print(2, l);
    strftime(l, sizeof(l), "%Y", lt);
    Screen.print(3, l);
}

// --- 12. Step counter --------------------------------------------------------
static uint32_t gSteps = 0;
static float    gLastMag = 0;
static bool     gAbove = false;
static void util_steps_enter() { gSteps = 0; drawHeader("Steps"); }
static void util_steps_tick() {
    if (!gAccGyro) return;
    int a[3] = {0,0,0};
    gAccGyro->getXAxes(a);
    float ax = a[0] / 1000.0f, ay = a[1] / 1000.0f, az = a[2] / 1000.0f;
    float mag = sqrtf(ax*ax + ay*ay + az*az);
    float threshHi = 1.20f, threshLo = 0.90f;
    if (!gAbove && mag > threshHi) { gAbove = true; gSteps++; }
    else if (gAbove && mag < threshLo) { gAbove = false; }
    gLastMag = mag;
    char l[24];
    snprintf(l, sizeof(l), "Steps: %lu", (unsigned long)gSteps);
    Screen.print(1, l);
    snprintf(l, sizeof(l), "Acc: %.2f g", mag);
    Screen.print(2, l);
    Screen.print(3, "A=next B=prev");
}

// --- 13. Tilt level ----------------------------------------------------------
static void util_level_enter() { drawHeader("Level"); }
static void util_level_tick() {
    if (!gAccGyro) return;
    int a[3] = {0,0,0};
    gAccGyro->getXAxes(a);
    float ax = a[0] / 1000.0f, ay = a[1] / 1000.0f, az = a[2] / 1000.0f;
    float pitch = atan2f(ax, sqrtf(ay*ay + az*az)) * 57.2958f;
    float roll  = atan2f(ay, sqrtf(ax*ax + az*az)) * 57.2958f;
    char l[24];
    snprintf(l, sizeof(l), "Pitch %+5.1f", pitch);
    Screen.print(1, l);
    snprintf(l, sizeof(l), "Roll  %+5.1f", roll);
    Screen.print(2, l);
    // simple bubble
    char bar[17] = "................";
    int center = 8;
    int off = (int)(roll / 5.0f);
    if (off < -7) off = -7; if (off > 7) off = 7;
    bar[center + off] = 'O';
    Screen.print(3, bar);
}

// --- 14. Compass -------------------------------------------------------------
static void util_compass_enter() { drawHeader("Compass"); }
static void util_compass_tick() {
    static uint64_t last = 0;
    uint64_t now = SystemTickCounterRead();
    if (now - last < 200) return;
    last = now;
    if (!gMag) return;
    int m[3] = {0,0,0};
    gMag->getMAxes(m);
    float heading = atan2f((float)m[1], (float)m[0]) * 57.2958f;
    if (heading < 0) heading += 360.0f;
    const char *cardinal = "N";
    if      (heading <  22.5f || heading >= 337.5f) cardinal = "N";
    else if (heading <  67.5f) cardinal = "NE";
    else if (heading < 112.5f) cardinal = "E";
    else if (heading < 157.5f) cardinal = "SE";
    else if (heading < 202.5f) cardinal = "S";
    else if (heading < 247.5f) cardinal = "SW";
    else if (heading < 292.5f) cardinal = "W";
    else                        cardinal = "NW";
    char l[24];
    snprintf(l, sizeof(l), "Heading: %3d", (int)heading);
    Screen.print(1, l);
    snprintf(l, sizeof(l), "Direction: %s", cardinal);
    Screen.print(2, l);
    Screen.print(3, "calib by rotating");
}

// --- 16. Noise meter (very rough, no mic DMA) --------------------------------
static void util_noise_enter() { drawHeader("Noise"); }
static void util_noise_tick() {
    // The AZ3166 microphone needs DMA setup via AudioClass for real audio.
    // For a lightweight indicator we sample analogRead on the audio input
    // pin (A2 routes to mic on dev kit) — a placeholder until DMA is wired.
    static uint64_t last = 0;
    uint64_t now = SystemTickCounterRead();
    if (now - last < 100) return;
    last = now;
    long sum = 0, sumSq = 0;
    const int N = 64;
    for (int i = 0; i < N; i++) {
        int v = analogRead(A2);
        sum += v;
    }
    int mean = (int)(sum / N);
    for (int i = 0; i < N; i++) {
        int v = analogRead(A2) - mean;
        sumSq += (long)v * v;
    }
    float rms = sqrtf((float)sumSq / N);
    char l[24];
    snprintf(l, sizeof(l), "RMS: %4d", (int)rms);
    Screen.print(1, l);
    int bars = (int)(rms / 50);
    if (bars > 16) bars = 16;
    char bar[17]; for (int i = 0; i < 16; i++) bar[i] = (i < bars) ? '#' : '.'; bar[16] = '\0';
    Screen.print(2, bar);
    Screen.print(3, "approximate");
}

// --- Blank/stub utilities ----------------------------------------------------
static void util_blank_enter() { drawHeader("--"); }
static void util_blank_tick()  { }

// --- 20. Local web dashboard -------------------------------------------------
//  Hosts a tiny HTTP server on port 80.  Returns a small HTML page (GET /)
//  with auto-refreshing live sensor values, plus JSON at GET /api.
//  The server is started in setup() so it remains reachable regardless of
//  which gadget is currently displayed; the WebDash gadget just shows status.

static void webReadSensors(float &temp, float &hum, float &pres, int axes[3]) {
    temp = 0; hum = 0; pres = 0; axes[0] = axes[1] = axes[2] = 0;
    if (gTempHum)  { gTempHum->getTemperature(&temp); gTempHum->getHumidity(&hum); }
    if (gPressure) { gPressure->getPressure(&pres); }
    if (gAccGyro)  { gAccGyro->getXAxes(axes); }
}

static void util_webdash_enter() {
    drawHeader("WebDash");
    if (!gWebServer) {
        Screen.print(1, "No WiFi");
        Screen.print(2, "Cannot start");
        return;
    }
    char l[24];
    snprintf(l, sizeof(l), "http://%s", gWebIp);
    Screen.print(1, l);
    snprintf(l, sizeof(l), "hits: %d", gWebHits);
    Screen.print(2, l);
    Screen.print(3, "GET / or /api");
}

static void util_webdash_tick() {
    if (!gWebServer) return;
    WiFiClient client = gWebServer->available();
    if (client) {
        // Read & discard request line + headers (cap to avoid hangs).
        uint32_t deadline = (uint32_t)SystemTickCounterRead() + 800;
        char reqLine[80]; int reqLen = 0; bool gotLine = false;
        while ((uint32_t)SystemTickCounterRead() < deadline) {
            int a = client.available();
            if (a <= 0) { delay(2); continue; }
            int c = client.read();
            if (c < 0) break;
            if (!gotLine) {
                if (c == '\r') continue;
                if (c == '\n') { reqLine[reqLen] = 0; gotLine = true; continue; }
                if (reqLen < (int)sizeof(reqLine) - 1) reqLine[reqLen++] = (char)c;
            } else {
                // Once we have the request line, drain a bit more then bail.
                if (a < 2) break;
            }
        }
        if (!gotLine) reqLine[0] = 0;
        bool isApi = strstr(reqLine, " /api") != nullptr;

        float t, h, p; int axes[3];
        webReadSensors(t, h, p, axes);
        char body[420];
        const char *ctype;
        if (isApi) {
            ctype = "application/json";
            snprintf(body, sizeof(body),
                "{\"tempC\":%.1f,\"humidity\":%.1f,\"pressure_hPa\":%.1f,"
                "\"accel\":{\"x\":%d,\"y\":%d,\"z\":%d},"
                "\"rssi\":%d,\"hits\":%d}",
                t, h, p, axes[0], axes[1], axes[2], (int)WiFi.RSSI(), gWebHits + 1);
        } else {
            ctype = "text/html";
            snprintf(body, sizeof(body),
                "<!doctype html><html><head><meta charset=utf-8>"
                "<meta http-equiv=refresh content=2>"
                "<title>DingDong</title></head><body style='font-family:sans-serif'>"
                "<h2>DingDong sensors</h2>"
                "<p>Temp: <b>%.1f C</b><br>Humidity: <b>%.1f %%</b><br>"
                "Pressure: <b>%.1f hPa</b><br>Accel: %d / %d / %d<br>"
                "WiFi RSSI: %d dBm<br>Hits: %d</p>"
                "<p><a href=/api>JSON</a></p></body></html>",
                t, h, p, axes[0], axes[1], axes[2], (int)WiFi.RSSI(), gWebHits + 1);
        }
        char header[160];
        int blen = (int)strlen(body);
        int hlen = snprintf(header, sizeof(header),
            "HTTP/1.1 200 OK\r\nContent-Type: %s\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
            ctype, blen);
        client.write((const unsigned char *)header, hlen);
        client.write((const unsigned char *)body, blen);
        client.stop();
        gWebHits++;

        // Refresh the OLED hit counter without flicker.
        char l[24];
        snprintf(l, sizeof(l), "hits: %d", gWebHits);
        Screen.print(2, l);
    }
}

static void util_stub_enter() {
    const Utility *u = findUtilityById(gEnabled[gCurrentIdx]);
    drawHeader(u ? u->shortName : "Stub");
    Screen.print(1, "Not implemented");
    Screen.print(2, "Needs config or");
    Screen.print(3, "external service");
}
static void util_stub_tick() { }

// --- 21. @Mentions counter ---------------------------------------------------
//  Values are filled from the /config response in setup() (single round-trip,
//  to avoid opening a second HTTP socket which destabilises the AZ3166).
//  The gadget simply renders the cached counts; press Reset to refresh.

static void mentionsDraw() {
    drawHeader("@Mentions");
    char l[24];
    if (gMentionsChats < 0 && gMentionsEmails < 0) {
        Screen.print(1, "Fetching...");
        Screen.print(2, "");
        Screen.print(3, "");
        return;
    }
    if (gMentionsTrackChats) {
        snprintf(l, sizeof(l), "Chats:  %d", gMentionsChats < 0 ? 0 : gMentionsChats);
    } else {
        snprintf(l, sizeof(l), "Chats:  off");
    }
    Screen.print(1, l);
    if (gMentionsTrackEmails) {
        snprintf(l, sizeof(l), "Emails: %d", gMentionsEmails < 0 ? 0 : gMentionsEmails);
    } else {
        snprintf(l, sizeof(l), "Emails: off");
    }
    Screen.print(2, l);
    int total = (gMentionsTrackChats  ? (gMentionsChats  < 0 ? 0 : gMentionsChats ) : 0)
              + (gMentionsTrackEmails ? (gMentionsEmails < 0 ? 0 : gMentionsEmails) : 0);
    snprintf(l, sizeof(l), "Total:  %d", total);
    Screen.print(3, l);

    // Red LED pulse when there are unread mentions, off otherwise.
    if (total > 0) gRgb.setColor(40, 0, 0);
    else           gRgb.turnOff();
}

static void util_mentions_enter() {
    // Values are fetched once in setup() (sync-on-startup pattern). The
    // gadget just displays the cached counts; press Reset on the device to
    // refresh from /mentions.
    mentionsDraw();
}

static void util_mentions_tick() {
    // Counts are refreshed in background by the periodic WiFi cycle in loop().
    // Redraw once per second so the display reflects the latest values.
    static uint64_t lastDrawMs = 0;
    uint64_t now = SystemTickCounterRead();
    if ((uint32_t)(now - lastDrawMs) >= 1000) {
        lastDrawMs = now;
        mentionsDraw();
    }
}
