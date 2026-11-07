// SPDX-License-Identifier: GPL-2.0-or-later

namespace ArkPar.Screens;

/// <summary>
/// Hands each progress update to the thread that created it (the screen's), since the engine
/// reports from the thread that reads its output. With no such thread (tests), it hands it over at once.
/// </summary>
internal sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Report(T value)
    {
        if (_context is null)
        {
            handler(value);
        }
        else
        {
            _context.Post(_ => handler(value), null);
        }
    }
}
