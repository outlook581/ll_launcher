using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ll_launcher.Models;
using ll_launcher.Data;
using ll_launcher.Services;

namespace ll_launcher;
public sealed class IconKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Application.Current.TryFindResource("Icon." + value) ?? Application.Current.FindResource("Icon.folder");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed record GroupView(long Id, string Name, string IconKey, Visibility LabelVisibility);
public sealed class ItemView(LauncherItem item, double rowWidth, LauncherRepository repository)
{
    public LauncherItem Item { get; } = item;
    public string Name => Item.Name;
    public string Target => Item.Kind == ShortcutSnapshot.Kind ? "已保存的快捷方式 · " + Path.GetFileName(Item.Target) : Item.Target;
    public bool IsFavorite => Item.IsFavorite;
    public string StarKey => IsFavorite ? "starFilled" : "star";
    public double RowWidth { get; } = rowWidth;
    public string FallbackKey => Item.Kind == "url" ? "link" : Item.Target == "ms-settings:" ? "wrench" : Item.Kind == "folder" ? "folder" : "file";
    public Visibility FallbackVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public ImageSource? Icon => ShellService.GetIcon(repository.LaunchTarget(Item), 48);
}

public sealed record ItemRow(List<ItemView> Items, int Columns, DataTemplate Template);
internal sealed partial class EditDialog : Window
{
    private readonly Dictionary<string, TextBox> _inputs = new();
    public Func<bool>? Validate { get; set; }

    public EditDialog(Window owner, string title)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        Width = Math.Clamp(owner.ActualWidth - 32, 276, 340);
        MaxHeight = Math.Min(Math.Max(250, owner.ActualHeight - 26), SystemParameters.WorkArea.Height);
        Loaded += (_, _) => _inputs.Values.FirstOrDefault()?.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Validate?.Invoke() != false) DialogResult = true;
    }

    private static void Label(Panel panel, string label)
    {
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 3) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(text);
    }

    public void AddField(string key, string label, string value = "", bool advanced = false)
    {
        var panel = advanced ? AdvancedFields : Fields;
        if (advanced)
        {
            Advanced.Visibility = Visibility.Visible;
            if (value.Length > 0) Advanced.IsExpanded = true;
        }
        Label(panel, label);
        var input = new TextBox { Text = value, Padding = new Thickness(8, 3, 8, 3), MinHeight = 27, VerticalContentAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
        _inputs.Add(key, input);
    }

    public ComboBox AddChoices(string label, IEnumerable<KeyValuePair<string, string>> choices, string selected)
    {
        Label(Fields, label);
        var combo = new ComboBox
        {
            Style = (Style)FindResource("GroupChoice"), ItemsSource = choices.ToList(),
            DisplayMemberPath = "Value", SelectedValuePath = "Key", SelectedValue = selected, Height = 28
        };
        System.Windows.Automation.AutomationProperties.SetName(combo, label);
        Fields.Children.Add(combo);
        return combo;
    }

    public ListBox ChooseIcon(IReadOnlyDictionary<string,string> choices, string selected)
    {
        IconPanel.Visibility = Visibility.Visible;
        IconChoices.ItemsSource = choices;
        IconChoices.SelectedValue = selected;
        return IconChoices;
    }

    public string Value(string key) => _inputs[key].Text.Trim();
    public bool Error(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }
}
