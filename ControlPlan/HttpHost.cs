using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlPlan;

/// <summary>
/// Tiny HTTP server. Serves the current AppSettings as JSON on GET /config
/// and the live mention counts on GET /mentions. All data endpoints are
/// gated by:
///   1. Binding to a single configured interface (AppSettings.ListenAddress).
///   2. A shared-secret token sent by the device in the X-DingDong-Token
///      header (AppSettings.AuthToken, constant-time compared).
///   3. An optional source-IP allowlist (AppSettings.AllowedDeviceIp).
/// /health is exempt from auth so the user can curl it from a browser to
/// confirm the server is up.
/// </summary>
public sealed class HttpHost : IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    // Recently-seen client IPs (authorised AND rejected) shown on the
    // Security tab so the user can tick which ones to allow.
    private readonly object _seenLock = new();
    private readonly Dictionary<string, SeenClient> _seenClients = new();

    public event EventHandler<string>? OnLog;
    public event EventHandler? OnSeenClientsChanged;

    public sealed record SeenClient(string Ip, DateTime LastSeenUtc, int Count, bool LastAccepted);

    public HttpHost(Func<AppSettings> getSettings)
    {
        _getSettings = getSettings;
    }

    /// <summary>Snapshot of recently-seen client IPs (latest first).</summary>
    public IReadOnlyList<SeenClient> GetSeenClients()
    {
        lock (_seenLock)
        {
            return _seenClients.Values
                .OrderByDescending(c => c.LastSeenUtc)
                .ToList();
        }
    }

    private void RecordSeen(string ip, bool accepted)
    {
        lock (_seenLock)
        {
            _seenClients.TryGetValue(ip, out var existing);
            _seenClients[ip] = new SeenClient(
                ip,
                DateTime.UtcNow,
                (existing?.Count ?? 0) + 1,
                accepted);
        }
        OnSeenClientsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }

    public void Start(int port)
    {
        Stop();
        Port = port;
        var addr = (_getSettings().ListenAddress ?? "").Trim();
        if (string.IsNullOrEmpty(addr) || addr == "0.0.0.0") addr = "+";
        var prefix = $"http://{addr}:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        try
        {
            _listener.Start();
            OnLog?.Invoke(this, $"HTTP server listening on {prefix}. Endpoints: /config /mentions /health");
        }
        catch (HttpListenerException ex)
        {
            // Most common reason: no URL ACL for non-loopback prefix (error 5 / 87 depending
            // on Windows build). Fall back to localhost so the app stays usable, and surface
            // a copy-pasteable netsh command in the log + message dialog.
            var hint = $"Could not bind {prefix} ({ex.Message.TrimEnd('.')}). " +
                       $"Falling back to localhost only.\r\n\r\n" +
                       $"To allow LAN access from the device, open an admin PowerShell once and run:\r\n" +
                       $"  netsh http add urlacl url={prefix} user=Everyone";
            OnLog?.Invoke(this, hint.Replace("\r\n", " | "));
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException(hint + "\r\n\r\n(localhost fallback also failed: " + inner.Message + ")", inner);
            }
        }
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Builds the netsh command that reserves the given prefix for the current
    /// user. Surfaced in the UI so the user can copy-paste into an elevated shell.
    /// </summary>
    public static string GetUrlAclCommand(string listenAddress, int port)
    {
        var addr = string.IsNullOrWhiteSpace(listenAddress) ? "+" : listenAddress.Trim();
        return $"netsh http add urlacl url=http://{addr}:{port}/ user=Everyone";
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _runTask?.Wait(500); } catch { }
        _listener = null;
        _cts = null;
        _runTask = null;
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var remoteIp = ctx.Request.RemoteEndPoint?.Address?.ToString() ?? "?";
            OnLog?.Invoke(this, $"{ctx.Request.HttpMethod} {path} from {ctx.Request.RemoteEndPoint}");

            // /health is intentionally unauthenticated so it can be curled
            // from a browser to confirm the server is up.
            var s = _getSettings();
            bool isHealth = path.Equals("/health", StringComparison.OrdinalIgnoreCase);
            if (!isHealth)
            {
                if (!AuthorizeRequest(ctx, s, out var reason))
                {
                    OnLog?.Invoke(this, $"  rejected: {reason}");
                    RecordSeen(remoteIp, accepted: false);
                    ctx.Response.StatusCode = reason.StartsWith("ip") ? 403 : 401;
                    ctx.Response.Close();
                    return;
                }
                RecordSeen(remoteIp, accepted: true);
            }

            if (path.Equals("/config", StringComparison.OrdinalIgnoreCase))
            {
                var dto = new
                {
                    enabled = s.Enabled.OrderBy(x => x).ToArray(),
                    chats = s.MentionsTrackChats ? s.MentionsChats : 0,
                    emails = s.MentionsTrackEmails ? s.MentionsEmails : 0,
                    trackChats = s.MentionsTrackChats,
                    trackEmails = s.MentionsTrackEmails,
                };
                var json = JsonSerializer.Serialize(dto);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path.Equals("/mentions", StringComparison.OrdinalIgnoreCase))
            {
                var dto = new
                {
                    chats = s.MentionsTrackChats ? s.MentionsChats : 0,
                    emails = s.MentionsTrackEmails ? s.MentionsEmails : 0,
                    trackChats = s.MentionsTrackChats,
                    trackEmails = s.MentionsTrackEmails,
                };
                var json = JsonSerializer.Serialize(dto);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = Encoding.UTF8.GetBytes("ok");
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
        }
        catch (Exception ex) { OnLog?.Invoke(this, "error: " + ex.Message); }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    public static IEnumerable<string> GetLocalIPv4Addresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                {
                    yield return ua.Address.ToString();
                }
            }
        }
    }

    /// <summary>
    /// Validates the X-DingDong-Token header (when configured) and the
    /// remote IP allowlist (when configured). Returns false with a short
    /// machine-readable reason ("token" or "ip:...") that the caller maps
    /// to a 401 or 403.
    /// </summary>
    private static bool AuthorizeRequest(HttpListenerContext ctx, AppSettings s, out string reason)
    {
        // 1. Token check (skipped when token is empty -- security disabled).
        if (!string.IsNullOrEmpty(s.AuthToken))
        {
            var provided = ctx.Request.Headers["X-DingDong-Token"] ?? string.Empty;
            var expected = s.AuthToken;
            var a = Encoding.UTF8.GetBytes(provided);
            var b = Encoding.UTF8.GetBytes(expected);
            if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
            {
                reason = "token mismatch";
                return false;
            }
        }

        // 2. Source-IP allowlist (loopback is always allowed for diagnostics).
        var allowed = s.AllowedDeviceIps ?? new List<string>();
        if (allowed.Count > 0)
        {
            var remote = ctx.Request.RemoteEndPoint?.Address;
            if (remote == null) { reason = "ip: no remote endpoint"; return false; }
            if (!IPAddress.IsLoopback(remote))
            {
                bool match = false;
                foreach (var a in allowed)
                {
                    if (IPAddress.TryParse(a, out var ip) && remote.Equals(ip)) { match = true; break; }
                }
                if (!match)
                {
                    reason = $"ip {remote} not in allowlist ({allowed.Count} entries)";
                    return false;
                }
            }
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// Generates a cryptographically random URL-safe token (~32 chars).
    /// Used to seed AppSettings.AuthToken on first run and from the
    /// "Regenerate" button in the Security tab.
    /// </summary>
    public static string GenerateToken()
    {
        Span<byte> buf = stackalloc byte[24];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
