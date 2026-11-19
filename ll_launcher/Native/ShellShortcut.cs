using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace ll_launcher.Native;

[SupportedOSPlatform("windows")]
internal static class ShellShortcut
{
    internal static string? GetFileSystemTarget(string shortcutPath)
    {
        var shellItemId = typeof(IShellItem).GUID;
        var linkTargetHandler = new Guid("3981e228-f559-11d3-8e3a-00c04f6837d5"); // BHID_LinkTargetItem
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(shortcutPath, IntPtr.Zero, ref shellItemId, out var link));
        IBindCtx? context = null;
        IShellItem? target = null;
        try
        {
            // STR_DONT_RESOLVE_LINK reads the stored target without searching for moved files.
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
            context.RegisterObjectParam("Don't Resolve Link", link);
            if (link.BindToHandler(context, ref linkTargetHandler, ref shellItemId, out target) < 0)
                return null;
            const uint FileSystemPath = 0x80058000; // SIGDN_FILESYSPATH
            if (target.GetDisplayName(FileSystemPath, out var path) < 0) return null;
            return Path.Exists(path) ? path : null;
        }
        finally
        {
            if (target != null) Marshal.ReleaseComObject(target);
            if (context != null) Marshal.ReleaseComObject(context);
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IBindCtx bindContext, ref Guid handler, ref Guid interfaceId, out IShellItem item);
        [PreserveSig]
        int GetParent(out IShellItem parent);
        [PreserveSig]
        int GetDisplayName(uint nameType, [MarshalAs(UnmanagedType.LPWStr)] out string path);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItem item);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx context);
}
