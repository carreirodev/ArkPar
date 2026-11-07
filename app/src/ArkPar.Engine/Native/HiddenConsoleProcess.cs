// SPDX-License-Identifier: GPL-2.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static ArkPar.Engine.Native.NativeMethods;

namespace ArkPar.Engine.Native;

/// <summary>
/// A child process attached to its own hidden console (a pseudoconsole: no window, ever).
/// The engine reads its cancel, pause and resume keys from the console, not from standard input,
/// so <see cref="SendKey"/> types them into that console. What the process prints goes to a plain
/// pipe instead (<see cref="Output"/>), byte for byte, without the console redrawing it.
/// </summary>
internal sealed class HiddenConsoleProcess : IDisposable
{
    // Columns and rows of the hidden console. Nothing is printed to it, so the size does not matter.
    private const int ConsoleSize = (25 << 16) | 120;

    private readonly IntPtr _pseudoConsole;
    private readonly FileStream _keyboard;
    private readonly Thread _consoleDrain;
    private readonly SafeProcessHandle _process;
    private readonly Lock _keyboardLock = new();
    private bool _disposed;

    private HiddenConsoleProcess(IntPtr pseudoConsole, FileStream keyboard, Thread consoleDrain, SafeProcessHandle process, FileStream output)
    {
        _pseudoConsole = pseudoConsole;
        _keyboard = keyboard;
        _consoleDrain = consoleDrain;
        _process = process;
        Output = output;
    }

    /// <summary>Everything the process writes to standard output and standard error.</summary>
    public Stream Output { get; }

    /// <summary>Starts <paramref name="exePath"/> with <paramref name="arguments"/>, quoted for the C runtime.</summary>
    public static HiddenConsoleProcess Start(string exePath, IEnumerable<string> arguments, string? workingDirectory = null)
    {
        // Console input: we write keys into one end, the pseudoconsole reads the other.
        // Console output: the pseudoconsole writes screen updates; we only drain them so it never blocks.
        CreatePipeOrThrow(out var consoleInputRead, out var consoleInputWrite);
        CreatePipeOrThrow(out var consoleOutputRead, out var consoleOutputWrite);
        IntPtr pseudoConsole;
        using (consoleInputRead)
        using (consoleOutputWrite)
        {
            var hresult = CreatePseudoConsole(ConsoleSize, consoleInputRead, consoleOutputWrite, 0, out pseudoConsole);
            if (hresult != 0)
            {
                consoleInputWrite.Dispose();
                consoleOutputRead.Dispose();
                throw new Win32Exception(hresult, "Could not create the hidden console for the engine.");
            }
        }

        var consoleDrain = new Thread(() => Drain(consoleOutputRead)) { IsBackground = true, Name = "Engine console drain" };
        consoleDrain.Start();

        CreatePipeOrThrow(out var outputRead, out var outputWrite);
        try
        {
            var process = CreateAttachedProcess(exePath, arguments, workingDirectory, pseudoConsole, outputWrite);
            return new HiddenConsoleProcess(
                pseudoConsole,
                new FileStream(consoleInputWrite, FileAccess.Write, bufferSize: 0),
                consoleDrain,
                process,
                new FileStream(outputRead, FileAccess.Read, bufferSize: 0));
        }
        catch
        {
            outputRead.Dispose();
            consoleInputWrite.Dispose();
            ClosePseudoConsole(pseudoConsole);
            consoleDrain.Join();
            throw;
        }
        finally
        {
            // The child holds its own copy now; ours must go, or reading Output never reaches the end.
            outputWrite.Dispose();
        }
    }

    /// <summary>Types one key into the hidden console, as if pressed on the keyboard.</summary>
    public void SendKey(char key)
    {
        lock (_keyboardLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _keyboard.WriteByte(checked((byte)key));
            _keyboard.Flush();
        }
    }

    /// <summary>Waits for the process to end and returns its exit code.</summary>
    public Task<int> WaitForExitAsync()
    {
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new ProcessWaitHandle(_process);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, _) =>
            {
                registration?.Unregister(null);
                waitHandle.Dispose();
                if (GetExitCodeProcess(_process, out var exitCode))
                {
                    exited.TrySetResult(unchecked((int)exitCode));
                }
                else
                {
                    exited.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the engine's exit code."));
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);
        return exited.Task;
    }

    /// <summary>Ends the process at once. Does nothing if it has already ended.</summary>
    public void Kill()
    {
        if (!TerminateProcess(_process, exitCode: 1)
            && GetExitCodeProcess(_process, out var exitCode) && exitCode == StillActive)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not stop the engine.");
        }
    }

    public void Dispose()
    {
        lock (_keyboardLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Closing the console also ends a process still attached to it.
        ClosePseudoConsole(_pseudoConsole);
        _consoleDrain.Join();
        _keyboard.Dispose();
        Output.Dispose();
        _process.Dispose();
    }

    private static SafeProcessHandle CreateAttachedProcess(
        string exePath, IEnumerable<string> arguments, string? workingDirectory, IntPtr pseudoConsole, SafeFileHandle outputWrite)
    {
        if (!SetHandleInformation(outputWrite, HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not prepare the engine's output pipe.");
        }

        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        var inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size);
        var outputAddRef = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not prepare the engine's start attributes.");
            }

            try
            {
                outputWrite.DangerousAddRef(ref outputAddRef);
                var output = outputWrite.DangerousGetHandle();

                // Only the output pipe is inherited. Without this list the engine would also inherit
                // every other inheritable handle open in this process at that moment, such as another
                // engine's output pipe, which then never reports its end.
                Marshal.WriteIntPtr(inheritedHandles, output);
                if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, inheritedHandles, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)
                    || !UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributePseudoConsole, pseudoConsole, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not prepare the engine's start attributes.");
                }

                var startupInfo = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo
                    {
                        Cb = Marshal.SizeOf<StartupInfoEx>(),
                        Flags = StartfUseStdHandles,
                        StdOutput = output,
                        StdError = output,
                    },
                    AttributeList = attributeList,
                };
                var commandLine = (CommandLine.Quote(exePath) + " " + CommandLine.Join(arguments) + "\0").ToCharArray();
                if (!CreateProcess(
                        exePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: true,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out var processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not start '{exePath}'.");
                }

                CloseHandle(processInformation.Thread);
                return new SafeProcessHandle(processInformation.Process, ownsHandle: true);
            }
            finally
            {
                DeleteProcThreadAttributeList(attributeList);
            }
        }
        finally
        {
            if (outputAddRef)
            {
                outputWrite.DangerousRelease();
            }

            Marshal.FreeHGlobal(inheritedHandles);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static void CreatePipeOrThrow(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a pipe for the engine.");
        }
    }

    private static void Drain(SafeFileHandle consoleOutput)
    {
        using var stream = new FileStream(consoleOutput, FileAccess.Read, bufferSize: 0);
        var buffer = new byte[4096];
        while (stream.Read(buffer) > 0)
        {
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
