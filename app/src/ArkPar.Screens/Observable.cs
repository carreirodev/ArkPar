// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArkPar.Screens;

/// <summary>Announces each property change, so the screen redraws what it binds to.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets <paramref name="field"/> and announces the change; returns whether it changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Announce(property);
        return true;
    }

    protected void Announce(string? property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
