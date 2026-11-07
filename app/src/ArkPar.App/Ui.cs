// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using ArkPar.Screens;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ArkPar.App;

/// <summary>Small conversions the screens' bindings call (x:Bind functions).</summary>
internal static class Ui
{
    public static Visibility VisibleUnless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfSet(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfItems(IEnumerable? items) => items?.GetEnumerator().MoveNext() == true ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfPositive(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Segoe Fluent Icons: Folder and Page.
    public static string IconOf(bool isFolder) => isFolder ? "" : "";

    public static string NumberOf(int count) => count.ToString("N0", PtBrazil.Culture);

    // While Percent is null the bar is indeterminate (IsPercentUnknown), so the stale value never shows.
    public static double PercentOf(double? percent) => percent ?? 0;

    // While Banner is null the bar is closed (HasBanner), so a stale severity never shows.
    public static InfoBarSeverity SeverityOf(Banner? banner) => banner?.Kind switch
    {
        BannerKind.Success => InfoBarSeverity.Success,
        BannerKind.Warning => InfoBarSeverity.Warning,
        BannerKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };
}
