// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Security;
using ArkPar.Screens;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace ArkPar.App;

/// <summary>
/// Windows notifications that tell how a create, verification or repair ended, for a user who was looking elsewhere.
/// Clicking one while ArkPar is open brings its window back.
/// </summary>
/// <remarks>
/// They use the notifications built into Windows, not the Windows App SDK's: those need the SDK installed on the PC
/// (its Singleton package), and the package carries the SDK inside instead (ADR-0002), so on a PC with nothing
/// installed they are not supported. Windows only needs to know the app's name, which ArkPar writes for the current
/// user. If Windows still refuses, ArkPar works the same, the screen shows how each task ended, and <c>failed</c>
/// says why there are no notifications.
/// </remarks>
internal sealed class TaskNotices
{
    // Who the notifications come from, as Windows knows it (an AppUserModelID).
    private const string AppId = "ArkPar";

    private readonly Action _bringBack;
    private readonly Action<Exception> _failed;
    private ToastNotifier? _notifier;

    // The last notification shown: kept, so its click still reaches this window.
    private ToastNotification? _shown;

    /// <param name="bringBack">Shows the window again; called on a background thread.</param>
    /// <param name="failed">Windows refused a notification; called on the thread that asked for it.</param>
    public TaskNotices(Action bringBack, Action<Exception> failed)
    {
        _bringBack = bringBack;
        _failed = failed;
        try
        {
            // The name Windows shows above each notification.
            using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId))
            {
                app.SetValue("DisplayName", "ArkPar");
            }

            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        }
        catch (Exception failure) when (failure is COMException or IOException or UnauthorizedAccessException or SecurityException)
        {
            failed(failure);
        }
    }

    /// <summary>A notification with what the screen now says, e.g. "A pasta está íntegra.".</summary>
    public void Show(Banner banner)
    {
        if (_notifier is null)
        {
            return;
        }

        var content = ToastNotificationManager.GetTemplateContent(banner.Detail is null ? ToastTemplateType.ToastText01 : ToastTemplateType.ToastText02);
        var lines = content.GetElementsByTagName("text");
        lines[0].AppendChild(content.CreateTextNode(banner.Title));
        if (banner.Detail is { } detail)
        {
            lines[1].AppendChild(content.CreateTextNode(detail));
        }

        var notice = new ToastNotification(content);
        notice.Activated += (_, _) => _bringBack();
        try
        {
            _notifier.Show(notice);
            _shown = notice;
        }
        catch (COMException failure)
        {
            _notifier = null;
            _failed(failure);
        }
    }
}
