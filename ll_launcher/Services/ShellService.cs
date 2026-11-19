using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ll_launcher.Services;

/// <summary>Windows Shell launch and bounded, frozen icon cache. Never executes a command interpreter.</summary>
public static class ShellService
{
    private const int CacheCapacity = 192;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> Recency = new();
    private sealed record CacheEntry(string Key, ImageSource? Image);

    public static void Launch(string target, string arguments = "", string workingDirectory = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ExpandEnvironmentVariables(target.Trim()),
            Arguments = arguments ?? "",
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = Environment.ExpandEnvironmentVariables(workingDirectory);
        // Launch the shortcut itself: this preserves its arguments, working directory and other Shell properties.
        using var process = Process.Start(startInfo);
    }

    internal static string? GetFileSystemTarget(string target)
    {
        var path = Environment.ExpandEnvironmentVariables(target.Trim());
        if (Path.IsPathFullyQualified(path)) return path;
        if (path.StartsWith("::{", StringComparison.Ordinal)) return null;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            return uri.IsFile ? uri.LocalPath : null;
        return path;
    }

    private static string ResolveSourcePath(string target, string workingDirectory)
    {
        var path = Path.GetFullPath(ResolveExecutablePath(target, workingDirectory));
        if (!Path.Exists(path))
        {
            // The Shell accepts bare program names such as "notepad" without the .exe suffix.
            var executable = Path.GetFileName(target) == target && !Path.HasExtension(target)
                ? ResolveExecutablePath(target + ".exe", workingDirectory) : null;
            if (!File.Exists(executable))
                throw new FileNotFoundException($"找不到源文件或文件夹：{path}", path);
            path = Path.GetFullPath(executable);
        }

        return path;
    }

    internal static void ShowProperties(IntPtr owner, string target, string workingDirectory)
    {
        var path = ResolveSourcePath(target, workingDirectory);
        const uint ShopFilePath = 2;
        if (!SHObjectProperties(owner, ShopFilePath, path, null))
            throw new InvalidOperationException($"Windows 无法显示该项目的属性：{path}");
    }

    internal static void OpenFileLocation(string target, string workingDirectory)
    {
        var path = ResolveSourcePath(target, workingDirectory);
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var item, 0, out _));
        try
        {
            // A zero selection count asks Explorer to open the item's parent and select the item itself.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    public static ImageSource? GetIcon(string target, int size = 48)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        size = Math.Clamp(size, 16, 256);
        target = Environment.ExpandEnvironmentVariables(target.Trim());
        string key = size + "|" + target;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                Recency.Remove(cached);
                Recency.AddFirst(cached);
                return cached.Value.Image;
            }
        }

        ImageSource? image = ExtractIcon(target, size);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var existing)) return existing.Value.Image;
            var node = Recency.AddFirst(new CacheEntry(key, image));
            Cache.Add(key, node);
            if (Cache.Count > CacheCapacity)
            {
                var oldest = Recency.Last!;
                Cache.Remove(oldest.Value.Key);
                Recency.RemoveLast();
            }
        }
        return image;
    }

    public static void ClearIconCache()
    {
        lock (CacheLock) { Cache.Clear(); Recency.Clear(); }
    }

    private static ImageSource? ExtractIcon(string target, int size)
    {
        IntPtr pidl = IntPtr.Zero;
        var info = new ShellFileInfo();
        try
        {
            uint flags = 0x00000100; // SHGFI_ICON: Shell resolves shortcut custom icons and folder desktop.ini.
            if (size <= 20) flags |= 0x00000001; // SHGFI_SMALLICON
            IntPtr result;
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                // Use the registered Internet shortcut icon without visiting the site or downloading a favicon.
                result = SHGetFileInfoPath("website.url", 0x80, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags | 0x00000010);
            }
            else if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("::{", StringComparison.Ordinal))
            {
                var largeIcon = ExtractShellItemIcon(target, size);
                if (largeIcon is not null) return largeIcon;
                if (SHParseDisplayName(target, IntPtr.Zero, out pidl, 0, out _) < 0 || pidl == IntPtr.Zero) return null;
                result = SHGetFileInfoPidl(pidl, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags | 0x00000008);
            }
            else
            {
                string path = uri is { IsFile: true } ? uri.LocalPath : ResolveExecutablePath(target);
                if (!File.Exists(path) && !Directory.Exists(path)) return null;
                var largeIcon = ExtractShellItemIcon(path, size);
                if (largeIcon is not null) return largeIcon;
                result = SHGetFileInfoPath(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
            }
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or COMException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static string ResolveExecutablePath(string target, string workingDirectory = "")
    {
        var localPath = !string.IsNullOrWhiteSpace(workingDirectory) && !Path.IsPathFullyQualified(target)
            ? Path.GetFullPath(target, Path.GetFullPath(Environment.ExpandEnvironmentVariables(workingDirectory)))
            : target;
        if (target.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || File.Exists(localPath) || Directory.Exists(localPath)) return localPath;
        // ShellExecute also finds bare executable names in system directories, PATH and App Paths.
        foreach (string directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.System) })
        {
            string candidate = Path.Combine(directory, target);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
            if (directory.Length == 0) continue;
            string candidate;
            try { candidate = Path.Combine(directory, target); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return candidate;
        }
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + target);
                    if (key?.GetValue(null) is string registered)
                    {
                        string candidate = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        }
        return localPath;
    }

    private static ImageSource? ExtractShellItemIcon(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory is null) return null;
            // ICONONLY avoids document thumbnails. BIGGERSIZEOK lets Shell choose a real 48/64/96/256 asset.
            if (factory.GetImage(new IconSize { Width = size, Height = size }, 0x00000005, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(size, size));
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconSize { public int Width; public int Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(IconSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? imageFactory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr owner, uint objectType, string path, string? propertyPage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoPath(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attributes, ref ShellFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr children, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
