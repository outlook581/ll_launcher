using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace ll_launcher;

internal sealed record Skin(string Id, string Name, ResourceDictionary Resources)
{
    public Brush Swatch => (Brush)Resources["AccentBrush"];
}

internal sealed class SkinCatalog
{
    private const string DefaultSkinId = "cloud";
    private static readonly string[] Roles =
    [
        "Top", "Bottom", "Sidebar", "Surface", "Raised", "Hover", "Border", "Divider",
        "Text", "Muted", "Accent", "Selected", "Action", "OnAction", "Thumb", "Favorite", "Error"
    ];
    public IReadOnlyList<Skin> Skins { get; }
    public Skin? Current { get; private set; }
    private SkinCatalog(List<Skin> skins) => Skins = skins;

    public static SkinCatalog Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var skins = new List<Skin>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString();
            var name = entry.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || skins.Any(s => s.Id == id))
                throw new InvalidDataException("皮肤名称或标识无效。");
            var palette = new ResourceDictionary();
            var colors = entry.GetProperty("colors");
            foreach (var role in Roles)
            {
                string? hex = colors.GetProperty(role).GetString();
                if (hex?.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var rgb))
                    throw new InvalidDataException($"皮肤「{name}」的 {role} 配色无效。");
                var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                palette.Add(role + "Brush", brush);
                if (role is "Top" or "Bottom") palette.Add(role + "Color", color);
            }
            skins.Add(new(id, name, palette));
        }
        if (skins.Count == 0) throw new InvalidDataException("未找到可用皮肤。");
        return new(skins);
    }

    public void Apply(string id)
    {
        var next = Skins.FirstOrDefault(s => s.Id == id)
            ?? Skins.FirstOrDefault(s => s.Id == DefaultSkinId)
            ?? Skins[0];
        if (next == Current) return;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (Current == null) dictionaries.Add(next.Resources);
        else dictionaries[dictionaries.IndexOf(Current.Resources)] = next.Resources;
        Current = next;
    }
}
