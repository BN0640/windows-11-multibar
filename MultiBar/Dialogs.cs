using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace MultiBar;

/// <summary>Lists every Start menu app (Store apps included) with search and multi-select.</summary>
public sealed class AppPickerWindow : Window
{
    public sealed class Row
    {
        public required ItemConfig Item { get; init; }
        public string Name => Item.Name;
        ImageSource? _icon; bool _loaded;
        public ImageSource? Icon { get { if (!_loaded) { _icon = Icons.For(Item, 32); _loaded = true; } return _icon; } }
    }

    readonly ListBox _list;
    readonly TextBox _search;
    readonly List<ItemConfig> _extra = new();
    public List<ItemConfig> Result { get; } = new();

    const string RowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Orientation="Horizontal" Margin="2,3">
            <CheckBox VerticalAlignment="Center" IsHitTestVisible="False" Focusable="False" MinWidth="0" Margin="0,0,6,0"
                      IsChecked="{Binding IsSelected, Mode=OneWay, RelativeSource={RelativeSource AncestorType=ListBoxItem}}"/>
            <Image Source="{Binding Icon}" Width="24" Height="24" Margin="0,0,10,0" RenderOptions.BitmapScalingMode="HighQuality"/>
            <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
          </StackPanel>
        </DataTemplate>
        """;

    AppPickerWindow(string title)
    {
        Title = title;
        Width = 480; Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = ThemeMode.System;
        Topmost = true;
        ShowInTaskbar = true;

        _search = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
        _search.SetValue(AutomationHelpText, "Pesquisar");
        var searchLabel = new TextBlock { Text = "Pesquisar apps", Margin = new Thickness(2, 0, 0, 4), Opacity = 0.8 };

        var rows = Launcher.InstalledApps().Select(i => new Row { Item = i }).ToList();
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = rows,
            ItemTemplate = (DataTemplate)XamlReader.Parse(RowTemplate),
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        var view = CollectionViewSource.GetDefaultView(rows);
        _search.TextChanged += (_, _) =>
        {
            var q = _search.Text.Trim();
            view.Filter = q.Length == 0 ? null : o => ((Row)o).Name.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        };

        var browse = new Button { Content = "Procurar arquivo...", Margin = new Thickness(0, 0, 8, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Escolha um programa ou atalho",
                Filter = "Programas e atalhos|*.exe;*.lnk;*.url;*.bat;*.cmd|Todos os arquivos|*.*",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) == true)
            {
                _extra.AddRange(dlg.FileNames.Select(Launcher.FromFile));
                Finish();
            }
        };
        var ok = new Button { Content = "Adicionar", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) => Finish();
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 110 };

        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        DockPanel.SetDock(browse, Dock.Left);
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(ok, Dock.Right);
        buttons.Children.Add(browse);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(searchLabel, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(searchLabel);
        root.Children.Add(_search);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        Loaded += (_, _) => { _search.Focus(); Keyboard.Focus(_search); };
        _list.MouseDoubleClick += (_, _) => Finish();
    }

    static readonly DependencyProperty AutomationHelpText = System.Windows.Automation.AutomationProperties.HelpTextProperty;

    void Finish()
    {
        Result.AddRange(_list.SelectedItems.Cast<Row>().Select(r => r.Item.CloneApp()));
        Result.AddRange(_extra);
        DialogResult = true;
    }

    public static List<ItemConfig> Pick(Window? owner, string title = "Adicionar apps")
    {
        var w = new AppPickerWindow(title);
        return w.ShowDialog() == true ? w.Result : new List<ItemConfig>();
    }
}

public sealed class InputDialog : Window
{
    readonly TextBox _box;

    InputDialog(string title, string label, string value)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = ThemeMode.System;
        Topmost = true;

        _box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 16) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        ok.SetResourceReference(StyleProperty, "AccentButtonStyle");
        ok.Click += (_, _) => { if (_box.Text.Trim().Length > 0) DialogResult = true; };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 100 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(Window? owner, string title, string label, string value)
    {
        var d = new InputDialog(title, label, value);
        return d.ShowDialog() == true ? d._box.Text.Trim() : null;
    }
}
