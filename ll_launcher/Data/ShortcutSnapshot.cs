using System.Text;

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

    internal static string? ReadInternetTarget(string shortcutPath)
    {
        // StreamReader detects a Unicode BOM. Older .url files may use the Windows ANSI code page.
        try { return ReadUrl(new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException)
        {
            var ansi = CodePagesEncodingProvider.Instance.GetEncoding(0);
            return ansi == null ? null : ReadUrl(ansi);
        }

        string? ReadUrl(Encoding encoding)
        {
            bool inShortcut = false;
            foreach (var line in File.ReadLines(shortcutPath, encoding))
            {
                var entry = line.Trim();
                if (entry.StartsWith('['))
                {
                    inShortcut = entry.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inShortcut) continue;
                int separator = entry.IndexOf('=');
                if (separator < 0 || !entry[..separator].TrimEnd().Equals("URL", StringComparison.OrdinalIgnoreCase)) continue;
                var address = entry[(separator + 1)..].Trim();
                // Windows INI values may enclose the full URL in matching quotes.
                if (address.Length >= 2 && address[0] is '\'' or '"' && address[^1] == address[0])
                    address = address[1..^1];
                return Uri.TryCreate(address, UriKind.Absolute, out _) ? address : null;
            }
            return null;
        }
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
