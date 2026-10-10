using System.Runtime.InteropServices;

namespace Optimiser;

// Windows groups taskbar buttons and pins by an app ID, which by default comes from the exe's path. The copy in
// Downloads and the one in Program Files count as different apps, so a pin made from one sits next to the window
// of the other. Giving the window one fixed ID (and saying how to relaunch it) keeps a single icon whichever
// copy or version is running. Only the window gets it, not the whole process, so the tray icon is untouched.
public static class Taskbar
{
    public const string AppId = "Lowballing.Optimiser";

    // exe: what a pin made from this window starts.
    public static void SetIdentity(IntPtr window, string exe)
    {
        var store = Store(window);
        try
        {
            Set(store, RelaunchCommand, $"\"{exe}\"");
            Set(store, RelaunchIcon, $"{exe},0");
            Set(store, RelaunchName, "Optimiser");
            Set(store, Id, AppId);
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    // Read back by the CI snapshot run, to prove the ID really lands on the window.
    public static string? GetId(IntPtr window)
    {
        var store = Store(window);
        try
        {
            var key = new PropertyKey(Id);
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try { return value.vt == LpWStr ? Marshal.PtrToStringUni(value.pointer) : null; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static IPropertyStore Store(IntPtr window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        return store;
    }

    static void Set(IPropertyStore store, uint pid, string text)
    {
        var key = new PropertyKey(pid);
        var value = new PropVariant { vt = LpWStr, pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { PropVariantClear(ref value); }
    }

    // PKEY_AppUserModel_* in propkey.h: one format ID, these property IDs.
    const uint RelaunchCommand = 2, RelaunchIcon = 3, RelaunchName = 4, Id = 5;
    const ushort LpWStr = 31; // VT_LPWSTR

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey(uint pid)
    {
        public Guid FormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        public uint Pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort vt;
        ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        IntPtr unused; // the union is 16 bytes on x64
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);
}
