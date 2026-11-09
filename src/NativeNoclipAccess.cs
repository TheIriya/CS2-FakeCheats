using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FakeCheats;

// ABI reference: the s2sdk revision pinned by CSS v1.0.379,
// d4a8b46676ded25b9d956287ec40ef3c9e12c14c (VEngineCvar007).
// ICvar::GetConCommandData is slot 44. ConCommandData starts with two
// pointers followed by uint64 flags. No callbacks are replaced/unregistered.
public sealed class NativeNoclipAccess : IDisposable
{
    private const ulong CheatFlag = 1UL << 14;
    private const int GetCommandDataSlot = 44;
    private IntPtr _commandData;
    private bool _removedCheatFlag;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr returnCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetCommandDataDelegate(IntPtr self, ulong commandReference);

    public void Enable()
    {
        if (_commandData != IntPtr.Zero) return;
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Native noclip requires a 64-bit server.");

        // Resolve the exact module already loaded by the engine. Opening the
        // same path acquires a temporary loader reference (required on Linux,
        // where a module base address is not a dlopen handle).
        using var process = Process.GetCurrentProcess();
        var module = process.Modules.Cast<ProcessModule>().FirstOrDefault(m =>
            m.ModuleName.Equals("tier0.dll", StringComparison.OrdinalIgnoreCase)
            || m.ModuleName.Equals("libtier0.so", StringComparison.Ordinal));
        if (module == null) throw new InvalidOperationException("The loaded tier0 module was not found.");
        if (!NativeLibrary.TryLoad(module.FileName, out var library))
            throw new InvalidOperationException("Could not acquire the loaded tier0 library.");
        try
        {
            if (!NativeLibrary.TryGetExport(library, "CreateInterface", out var factoryPointer))
                throw new InvalidOperationException("tier0 does not export CreateInterface.");
            var factory = Marshal.GetDelegateForFunctionPointer<CreateInterfaceDelegate>(factoryPointer);
            EnableFromInterface(factory("VEngineCvar007", IntPtr.Zero));
        }
        finally
        {
            // Release our reference only; the running engine retains its own.
            NativeLibrary.Free(library);
        }
    }

    internal void EnableFromInterface(IntPtr cvar)
    {
        if (_commandData != IntPtr.Zero) return;
        if (cvar == IntPtr.Zero) throw new InvalidOperationException("VEngineCvar007 is unavailable.");

        var vtable = Marshal.ReadIntPtr(cvar);
        var getData = Marshal.GetDelegateForFunctionPointer<GetCommandDataDelegate>(
            Marshal.ReadIntPtr(vtable, GetCommandDataSlot * IntPtr.Size));

        // ConCommandRef is an 8-byte value: uint16 access index, padding, int32
        // registration index. Enumerating data avoids platform differences in
        // the ABI for returning a C++ ConCommandRef from FindConCommand.
        var invalid = getData(cvar, ushort.MaxValue);
        for (uint index = 0; index < ushort.MaxValue; index++)
        {
            var data = getData(cvar, index);
            if (data == IntPtr.Zero || data == invalid) break;
            var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data));
            if (!string.Equals(name, "noclip", StringComparison.Ordinal)) continue;
            _commandData = data;
            var flags = ReadFlags(data);
            _removedCheatFlag = (flags & CheatFlag) != 0;
            if (_removedCheatFlag) WriteFlags(data, flags & ~CheatFlag);
            return;
        }
        throw new InvalidOperationException("The native noclip command was not found.");
    }

    public void Dispose()
    {
        if (_commandData == IntPtr.Zero) return;
        // Restore only the bit this plugin changed, preserving other plugins'
        // changes to unrelated flags. The engine owns the command allocation.
        if (_removedCheatFlag) WriteFlags(_commandData, ReadFlags(_commandData) | CheatFlag);
        _commandData = IntPtr.Zero;
        _removedCheatFlag = false;
    }

    private static ulong ReadFlags(IntPtr data) => unchecked((ulong)Marshal.ReadInt64(data, 2 * IntPtr.Size));
    private static void WriteFlags(IntPtr data, ulong flags) =>
        Marshal.WriteInt64(data, 2 * IntPtr.Size, unchecked((long)flags));
}
