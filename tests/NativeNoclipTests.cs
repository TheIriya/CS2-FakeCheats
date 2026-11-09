using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FakeCheats;

internal static unsafe class NativeNoclipTests
{
    private const ulong Cheat = 1UL << 14;
    private static IntPtr _data;
    private static IntPtr _invalid;
    private static IntPtr _name;

    // An unmanaged test vtable exercises the actual ABI call and native data
    // writes without loading a CS2 server or modifying engine memory.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr GetData(IntPtr self, ulong reference) => reference == 0 ? _data : _invalid;

    public static void Run(Action<bool, string> check)
    {
        var vtable = Marshal.AllocHGlobal(45 * IntPtr.Size);
        var instance = Marshal.AllocHGlobal(IntPtr.Size);
        _data = Marshal.AllocHGlobal(64);
        _invalid = Marshal.AllocHGlobal(64);
        _name = Marshal.StringToCoTaskMemUTF8("noclip");
        try
        {
            Marshal.Copy(new byte[45 * IntPtr.Size], 0, vtable, 45 * IntPtr.Size);
            Marshal.Copy(new byte[64], 0, _data, 64);
            Marshal.Copy(new byte[64], 0, _invalid, 64);
            Marshal.WriteIntPtr(vtable, 44 * IntPtr.Size,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, IntPtr>)&GetData);
            Marshal.WriteIntPtr(instance, vtable);
            Marshal.WriteIntPtr(_data, _name);
            void SetFlags(ulong flags) => Marshal.WriteInt64(_data, 16, unchecked((long)flags));
            ulong GetFlags() => unchecked((ulong)Marshal.ReadInt64(_data, 16));

            var original = Cheat | (1UL << 2) | (1UL << 19);
            SetFlags(original);
            using (var access = new NativeNoclipAccess())
            {
                access.EnableFromInterface(instance);
                check(GetFlags() == (original & ~Cheat), "Native noclip removes only FCVAR_CHEAT");
                check(Marshal.ReadIntPtr(_data) == _name, "Native noclip preserves the command name pointer");
                access.EnableFromInterface(instance);
                check(GetFlags() == (original & ~Cheat), "Native noclip initialization is idempotent");
                SetFlags(GetFlags() | (1UL << 32));
            }
            check(GetFlags() == (original | (1UL << 32)), "Unload restores the cheat bit and preserves unrelated changes");

            SetFlags(1UL << 19);
            var alreadyUnlocked = new NativeNoclipAccess();
            alreadyUnlocked.EnableFromInterface(instance);
            alreadyUnlocked.Dispose();
            alreadyUnlocked.Dispose();
            check(GetFlags() == (1UL << 19), "Unload does not add a cheat bit that was originally absent");

            using (var missingInterface = new NativeNoclipAccess())
            {
                var rejected = false;
                try { missingInterface.EnableFromInterface(IntPtr.Zero); }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected, "Missing ICvar interface fails without memory writes");
            }

            Marshal.FreeCoTaskMem(_name);
            _name = Marshal.StringToCoTaskMemUTF8("some_other_command");
            Marshal.WriteIntPtr(_data, _name);
            SetFlags(original);
            using (var missingCommand = new NativeNoclipAccess())
            {
                var rejected = false;
                try { missingCommand.EnableFromInterface(instance); }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected && GetFlags() == original, "Missing noclip never changes another command's flags");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(_name);
            Marshal.FreeHGlobal(_data);
            Marshal.FreeHGlobal(_invalid);
            Marshal.FreeHGlobal(instance);
            Marshal.FreeHGlobal(vtable);
        }
    }
}
