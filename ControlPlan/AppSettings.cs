using System.Text.Json.Serialization;

namespace ControlPlan;

/// <summary>
/// Catalogue of utilities the device can run. Keep IDs stable — firmware references them by number.
/// </summary>
public static class Utilities
{
    public sealed record Utility(int Id, string Name, string Description, bool Implemented);

    public static readonly Utility[] All =
    [
        new(1,  "Pomodoro timer",          "25 / 5 min countdown shown on OLED.", true),
        new(2,  "Stand-up reminder",       "Accelerometer-based inactivity alert.", false),
        new(3,  "Meeting DND sign",        "Toggle status, post to webhook.", false),
        new(4,  "Chess clock",             "Two-player tap-button timer (needs A/B as input).", false),
        new(5,  "Room comfort monitor",    "Temp / humidity / pressure on OLED.", true),
        new(6,  "Pressure-drop alert",     "Warns on rapid barometric drop.", false),
        new(7,  "Sleep env logger",        "Overnight temp/humidity log upload.", false),
        new(8,  "Build / CI status light", "GitHub Actions / DevOps poll, RGB indicator.", false),
        new(9,  "Server ping monitor",     "Pings N hosts, OLED shows up/down.", false),
        new(10, "WiFi surveyor",           "Live SSID / channel / RSSI scan.", true),
        new(11, "NTP wall clock",          "Big clock synced via NTP.", true),
        new(12, "Step counter",            "Accelerometer step count.", true),
        new(13, "Tilt level",              "Digital bubble level.", true),
        new(14, "Compass",                 "Tilt-compensated magnetic heading.", true),
        new(15, "Gesture macro pad",       "Shake/tilt -> HTTP shortcuts.", false),
        new(16, "Noise level meter",       "Microphone RMS in dB.", true),
        new(17, "IR universal blaster",    "Replay IR codes via on-board IR LED.", false),
        new(18, "Azure IoT telemetry",     "Stream sensors to Azure IoT Hub.", false),
        new(19, "MQTT publisher",          "Home Assistant auto-discovery.", false),
        new(20, "Local web dashboard",     "Sketch hosts HTTP server with sensor JSON.", true),
        new(21, "@Mentions counter",       "Shows unread Teams chats + emails where you are tagged (data from this app).", true),
    ];
}

public sealed class AppSettings
{
    [JsonPropertyName("port")]
    public int Port { get; set; } = 8088;

    // -------------------------------------------------------------
    //  Security knobs (see "Option A" in README -> Security)
    // -------------------------------------------------------------

    /// <summary>
    /// Interface the HTTP server binds to. Use:
    ///   ""  or "+"           -> any interface (requires URL ACL on Windows
    ///                            for non-loopback; falls back to localhost
    ///                            automatically when ACL is missing).
    ///   "localhost"          -> loopback only.
    ///   "192.168.x.y"        -> a specific LAN address. Recommended once you
    ///                            know the PC's stable LAN IP.
    /// </summary>
    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; set; } = "+";

    /// <summary>
    /// Shared secret sent by the firmware in the <c>X-DingDong-Token</c>
    /// header. The server rejects any data request without a matching token.
    /// Empty string = disabled (no token check). Auto-generated on first
    /// run if empty; rotate via the Security tab.
    /// </summary>
    [JsonPropertyName("authToken")]
    public string AuthToken { get; set; } = "";

    /// <summary>
    /// If non-empty, only requests originating from one of these IPv4
    /// addresses are served. Loopback (127.0.0.1) is always allowed
    /// regardless. Empty list = no IP check.
    /// </summary>
    [JsonPropertyName("allowedDeviceIps")]
    public List<string> AllowedDeviceIps { get; set; } = new();

    /// <summary>
    /// Legacy single-IP field. If present in an old appsettings.json it is
    /// migrated into <see cref="AllowedDeviceIps"/> on load and cleared.
    /// </summary>
    [JsonPropertyName("allowedDeviceIp")]
    public string AllowedDeviceIp { get; set; } = "";

    [JsonPropertyName("enabled")]
    public HashSet<int> Enabled { get; set; } = new(Utilities.All.Where(u => u.Implemented).Select(u => u.Id));

    // ---- @Mentions data exposed via GET /mentions to the device ----
    [JsonPropertyName("mentionsTrackChats")]
    public bool MentionsTrackChats { get; set; } = true;

    [JsonPropertyName("mentionsTrackEmails")]
    public bool MentionsTrackEmails { get; set; } = true;

    /// <summary>Manual count of unread Teams chats where you are @-mentioned.</summary>
    [JsonPropertyName("mentionsChats")]
    public int MentionsChats { get; set; } = 0;

    /// <summary>Manual count of unread emails where you are @-mentioned.</summary>
    [JsonPropertyName("mentionsEmails")]
    public int MentionsEmails { get; set; } = 0;

    /// <summary>When true, counts are refreshed from Windows Action Center notifications every 30 s instead of taken from the manual spinners.</summary>
    [JsonPropertyName("mentionsAuto")]
    public bool MentionsAuto { get; set; } = true;

    /// <summary>
    /// Comma-separated list of name tokens to look for in toast bodies, e.g.
    /// "mubi,mubi ali,mubashir".  A toast counts as an @-mention only if its
    /// body contains "@" followed by any of these tokens.  Defaults to the
    /// current Windows username.  Case-insensitive.
    /// </summary>
    [JsonPropertyName("mentionsNameAliases")]
    public string MentionsNameAliases { get; set; } = Environment.UserName;

    // ---- Window geometry (so the app reopens where you left it) ----
    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; } = 760;

    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; } = 880;

    [JsonPropertyName("windowX")]
    public int WindowX { get; set; } = -1; // -1 means "don't restore, use default screen position"

    [JsonPropertyName("windowY")]
    public int WindowY { get; set; } = -1;

    [JsonPropertyName("windowMaximized")]
    public bool WindowMaximized { get; set; } = false;
}
