namespace ll_launcher.Data;

// Keep the Shell's complete link, including its arguments, icon and launch flags.
// Preserve the Windows security zone when a link came from a downloaded file.
public sealed record ShortcutSnapshot(byte[] Contents, byte[]? Zone)
{
    public const string Kind = "shortcut";

    public static bool IsLinkPath(string target)
    {
        var path = Environment.ExpandEnvironmentVariables(target);
        return Path.IsPathFullyQualified(path) &&
            (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".url", StringComparison.OrdinalIgnoreCase));
    }

    public static ShortcutSnapshot Read(string source)
    {
        if (!IsLinkPath(source)) throw new ArgumentException("请指定快捷方式的完整路径。");
        var path = Environment.ExpandEnvironmentVariables(source);
        using var file = File.OpenRead(path);
        using var contents = new MemoryStream();
        file.CopyTo(contents);
        if (contents.Length == 0) throw new InvalidDataException("快捷方式为空，请重新选择文件。");
        var zonePath = path + ":Zone.Identifier";
        byte[]? zone;
        try { zone = File.ReadAllBytes(zonePath); }
        catch (FileNotFoundException) { zone = null; }
        return new(contents.ToArray(), zone);
    }

    public void WriteCache(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, Contents);
            if (Zone != null) File.WriteAllBytes(temp + ":Zone.Identifier", Zone);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
