using System.Runtime.Versioning;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace ControlPlan;

/// <summary>
/// Reads Windows Action Center notifications to estimate the number of unread
/// Teams chats / channel mentions and Outlook mails the user has waiting for
/// them.  Uses the UWP <c>UserNotificationListener</c> API, which any classic
/// .NET app on Windows 10 1809+ can call once the user grants
/// <i>"Let apps access your notifications"</i> in Settings &gt; Privacy.
///
/// Why this approach (vs Microsoft Graph)?
///  - No app registration, no OAuth, no admin consent.
///  - Works equally for classic + new Teams and classic + new Outlook,
///    plus Outlook for the Web if the user installed it as a PWA.
///  - Reads what the user actually sees on screen, so the count matches the
///    little red badge on their taskbar.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MentionsReader
{
    public enum AccessState { Unknown, Allowed, Denied, Unsupported }

    public AccessState Access { get; private set; } = AccessState.Unknown;
    public int TeamsCount { get; private set; }
    public int OutlookCount { get; private set; }
    public DateTime LastRefresh { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Comma-separated extra name tokens to look for (in addition to <c>Environment.UserName</c>).</summary>
    public string NameAliases { get; set; } = string.Empty;

    public async Task<AccessState> RequestAccessAsync()
    {
        try
        {
            var status = await UserNotificationListener.Current.RequestAccessAsync();
            Access = status switch
            {
                UserNotificationListenerAccessStatus.Allowed => AccessState.Allowed,
                UserNotificationListenerAccessStatus.Denied  => AccessState.Denied,
                _ => AccessState.Unknown,
            };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Access = AccessState.Unsupported;
        }
        return Access;
    }

    public async Task RefreshAsync()
    {
        LastError = null;
        try
        {
            if (Access != AccessState.Allowed)
            {
                await RequestAccessAsync();
                if (Access != AccessState.Allowed) return;
            }

            var listener = UserNotificationListener.Current;
            var toasts = await listener.GetNotificationsAsync(NotificationKinds.Toast);

            // Build the list of "@name" patterns to look for.  Anything in
            // NameAliases (comma-separated) plus the Windows username is fair
            // game.  Empty / whitespace entries are dropped.  Matching is
            // case-insensitive and only counts when an actual '@' precedes
            // the token, so "mubali@example.com" (a sender address) does NOT
            // qualify but "@Mubi Ali" in a message body does.
            var tokens = new List<string>();
            tokens.Add(Environment.UserName);
            if (!string.IsNullOrWhiteSpace(NameAliases))
            {
                tokens.AddRange(NameAliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }
            var patterns = tokens
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => "@" + t.TrimStart('@').ToLowerInvariant())
                .Distinct()
                .ToArray();

            int teams = 0, outlook = 0;
            foreach (var n in toasts)
            {
                var aumid = n.AppInfo?.AppUserModelId ?? string.Empty;
                var display = n.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
                var bucketKey = (aumid + " " + display).ToLowerInvariant();
                bool isTeams   = bucketKey.Contains("teams");
                bool isOutlook = bucketKey.Contains("outlook");
                if (!isTeams && !isOutlook) continue;

                // Concatenate every text element in the toast so we can search
                // sender / subject / body in one pass.
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) continue;
                var body = string.Join(" ", binding.GetTextElements().Select(t => t.Text ?? ""))
                                 .ToLowerInvariant();
                if (!patterns.Any(p => body.Contains(p))) continue;

                if (isTeams) teams++;
                else         outlook++;
            }
            TeamsCount = teams;
            OutlookCount = outlook;
            LastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }
}
