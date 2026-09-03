using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Findra;

/// <summary>
/// The terminal that started us, borrowed for the length of one diagnostic.
///
/// <para>Findra is a windows-subsystem binary, because a console-subsystem one is given a black
/// console window by Windows every time it is started without a terminal - from the installer,
/// from the Start menu, from an Explorer double-click, from the sign-in autostart entry, and once
/// more from the elevated logon task. A widget that drags a window like that behind it is not
/// shippable.</para>
///
/// <para>The price is that a windows-subsystem process has no standard output at all, even when a
/// person typed its name at a prompt. <c>AttachConsole(ATTACH_PARENT_PROCESS)</c> buys it back: it
/// joins the console the caller already owns, and it FAILS - harmlessly, changing nothing - when
/// there is none. That failure is the whole reason this is the right call and
/// <c>AllocConsole</c> is not: allocating would conjure the very window this exists to prevent, on
/// exactly the launches that have no terminal.</para>
///
/// <para>Redirection is the trap. Attaching resets the process's standard handles to the console's
/// own, so a mode whose output was being captured - <c>build/Check-Diagnostics.ps1</c> pipes every
/// one of them - would write to the window and hand its caller nothing. So the handles are read
/// BEFORE attaching and put back afterwards if they were already something: a pipe or a file wins
/// over the console, and the console is only opened for a handle that was not set at all.</para>
/// </summary>
internal static class ParentConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    /// <summary>
    /// Join the caller's console if there is one, and make <see cref="Console"/> write to it.
    /// Never throws and never allocates a console: a launch with no terminal is the normal case,
    /// not a failure.
    /// </summary>
    internal static void Borrow()
    {
        nint outBefore, errBefore;
        try
        {
            outBefore = GetStdHandle(StdOutputHandle);
            errBefore = GetStdHandle(StdErrorHandle);
            if (!AttachConsole(AttachParentProcess)) return;
        }
        catch (DllNotFoundException) { return; }
        catch (EntryPointNotFoundException) { return; }

        Reopen(StdOutputHandle, outBefore, Console.SetOut);
        Reopen(StdErrorHandle, errBefore, Console.SetError);
    }

    /// <summary>
    /// Give one standard stream back. A handle that was already set is restored, because it is a
    /// redirection somebody asked for. A handle that was not set is pointed at the console we have
    /// just joined, and <see cref="Console"/> is given a writer over it - the cached writer .NET
    /// would otherwise build lazily sits on the handle as it was, which was nothing.
    /// </summary>
    private static void Reopen(int which, nint before, Action<TextWriter> install)
    {
        if (Usable(before)) { SetStdHandle(which, before); return; }

        nint console = CreateFileW("CONOUT$", GenericRead | GenericWrite,
                                   FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (!Usable(console)) return;
        SetStdHandle(which, console);

        // ownsHandle: false - the handle stays a standard handle of the process for as long as it
        // runs, so a finaliser closing it would take the process's own output with it.
        // AutoFlush, because a diagnostic that exits without draining its buffer prints nothing.
        // The encoding is spelled out rather than inherited: Console.OutputEncoding recreates only
        // the writers it built itself, and this one is ours.
        var stream = new FileStream(new SafeFileHandle(console, ownsHandle: false), FileAccess.Write);
        install(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        });
    }

    private static bool Usable(nint handle) => handle != 0 && handle != -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int which, nint handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint security,
                                           uint disposition, uint flags, nint template);
}
