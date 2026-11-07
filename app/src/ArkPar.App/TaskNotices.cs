// SPDX-License-Identifier: GPL-2.0-or-later

using ArkPar.Screens;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ArkPar.App;

/// <summary>
/// Windows notifications that tell how a create, verification or repair ended, for a user who was looking elsewhere.
/// Clicking one brings the ArkPar window back.
/// </summary>
internal sealed class TaskNotices
{
    private readonly bool _registered;

    /// <param name="bringBack">Shows the window again; called on a background thread.</param>
    public TaskNotices(Action bringBack)
    {
        // Windows without notifications for this app (e.g. turned off by policy): the screen still shows how it ended.
        if (!AppNotificationManager.IsSupported())
        {
            return;
        }

        // Before Register, so a click reaches this process instead of starting another ArkPar.
        AppNotificationManager.Default.NotificationInvoked += (_, _) => bringBack();
        AppNotificationManager.Default.Register();
        _registered = true;
    }

    /// <summary>A notification with what the screen now says, e.g. "A pasta está íntegra.".</summary>
    public void Show(Banner banner)
    {
        if (!_registered)
        {
            return;
        }

        var builder = new AppNotificationBuilder().AddText(banner.Title);
        if (banner.Detail is { } detail)
        {
            builder.AddText(detail);
        }

        AppNotificationManager.Default.Show(builder.BuildNotification());
    }

    /// <summary>Called when the window closes, so a notification clicked later starts ArkPar again.</summary>
    public void Stop()
    {
        if (_registered)
        {
            AppNotificationManager.Default.Unregister();
        }
    }
}
