// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using ArkPar.Screens;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ArkPar.App;

/// <summary>
/// Windows notifications that tell how a create, verification or repair ended, for a user who was looking elsewhere.
/// Clicking one brings the ArkPar window back.
/// </summary>
/// <remarks>
/// Windows may refuse them (turned off by policy, or a Windows App SDK part missing on that PC). ArkPar then still
/// opens and works, the screen still shows how each task ended, and <c>failed</c> says why there are none.
/// </remarks>
internal sealed class TaskNotices
{
    private readonly Action<Exception> _failed;
    private bool _registered;

    /// <param name="bringBack">Shows the window again; called on a background thread.</param>
    /// <param name="failed">Windows refused a notification; called on the thread that asked for it.</param>
    public TaskNotices(Action bringBack, Action<Exception> failed)
    {
        _failed = failed;
        if (!AppNotificationManager.IsSupported())
        {
            failed(new NotSupportedException("Windows does not support app notifications for ArkPar here."));
            return;
        }

        try
        {
            // Before Register, so a click reaches this process instead of starting another ArkPar.
            AppNotificationManager.Default.NotificationInvoked += (_, _) => bringBack();
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (COMException failure)
        {
            failed(failure);
        }
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

        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (COMException failure)
        {
            _registered = false;
            _failed(failure);
        }
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
