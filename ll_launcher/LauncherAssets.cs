using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace ll_launcher;

internal static class LauncherAssets
{
    public static void Load(ResourceDictionary resources, string directory)
    {
        var icons = Path.Combine(directory, "Icons");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(icons, "sources.json")));
        XNamespace svg = "http://www.w3.org/2000/svg";
        foreach (var entry in manifest.RootElement.GetProperty("icons").EnumerateArray())
        {
            var key = entry.GetProperty("key").GetString();
            var file = entry.GetProperty("filename").GetString();
            if (key == null || !key.StartsWith("Icon.", StringComparison.Ordinal) || string.IsNullOrEmpty(file) ||
                file != Path.GetFileName(file) || !file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("图标清单包含无效的名称。");
            var source = XElement.Load(Path.Combine(icons, file));
            var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var path in source.Elements(svg + "path"))
            {
                string data = (string?)path.Attribute("d") ?? throw new InvalidDataException($"图标缺少轮廓：{file}");
                var rule = (string?)path.Attribute("fill-rule") ?? (string?)source.Attribute("fill-rule");
                geometry.Children.Add(Geometry.Parse((rule == "evenodd" ? "F0 " : "F1 ") + data));
            }
            if (geometry.Children.Count == 0) throw new InvalidDataException($"图标中找不到轮廓：{file}");
            geometry.Freeze();
            resources.Add(key, geometry);
        }
        using var stream = File.OpenRead(Path.Combine(directory, "Launcher.ico"));
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var brand = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        brand.Freeze();
        resources.Add("BrandIcon", brand);
    }
}
