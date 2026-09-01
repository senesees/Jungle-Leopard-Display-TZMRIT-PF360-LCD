using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Views.Overlay;

/// <summary>
/// Picks a sensor and inserts it as a template token.
///
/// Shows every source with its live value beside it, because the id alone
/// ("gpu.vram.load" versus "gpu.vram.percent") does not tell you which one you
/// want. This is what makes the text layer usable without documentation.
///
/// Built in code rather than XAML: it is one list and two buttons, and a .xaml
/// pair for that is more files than it is worth.
/// </summary>
public sealed class TokenPickerWindow : Window
{
    private readonly ListBox _list = new();
    private readonly TextBox _search = new();
    private readonly TextBox _format = new();
    private readonly List<SensorDescriptor> _shown = new();
    private readonly SensorRegistry _sensors;

    /// <summary>The token to insert, e.g. <c>{gpu.temp:0}</c>.</summary>
    public string? Token { get; private set; }

    public TokenPickerWindow(SensorRegistry sensors)
    {
        _sensors = sensors;

        Title = "Insert a sensor";
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush?)TryFindResource("Bg") ?? Brushes.Black;

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _search.Margin = new Thickness(0, 0, 0, 8);
        _search.TextChanged += (_, _) => Populate();
        Grid.SetRow(_search, 0);
        root.Children.Add(_search);

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.MouseDoubleClick += (_, _) => Accept();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        var fmtRow = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        fmtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fmtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var fmtLabel = new TextBlock
        {
            Text = "Format",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = (Brush?)TryFindResource("TextDim") ?? Brushes.Gray,
        };
        _format.Text = "0";
        _format.ToolTip = "A .NET number format: 0, 0.0, 0.00. Leave empty to let it choose.";

        Grid.SetColumn(fmtLabel, 0);
        Grid.SetColumn(_format, 1);
        fmtRow.Children.Add(fmtLabel);
        fmtRow.Children.Add(_format);
        Grid.SetRow(fmtRow, 2);
        root.Children.Add(fmtRow);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var ok = new Button
        {
            Content = "Insert",
            Style = TryFindResource("BtnPrimary") as Style,
            IsDefault = true,
        };
        ok.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel",
            Margin = new Thickness(8, 0, 0, 0),
            Style = TryFindResource("Btn") as Style,
            IsCancel = true,
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
        Populate();
        Loaded += (_, _) => _search.Focus();
    }

    private void Populate()
    {
        string q = _search.Text.Trim();
        SensorSnapshot snap = _sensors.Snapshot();

        _shown.Clear();
        var rows = new List<string>();

        foreach (SensorDescriptor d in _sensors.Descriptors
                     .OrderBy(d => d.Category)
                     .ThenBy(d => d.Id, StringComparer.Ordinal))
        {
            if (q.Length > 0
                && d.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                && d.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            SensorReading r = snap[d.Id];
            string value = !r.Available ? "--"
                : d.IsText ? r.Text ?? "--"
                : $"{r.Value:0.##} {d.Unit}";

            _shown.Add(d);
            rows.Add($"{d.Id,-24} {value,-16} {d.Name}");
        }

        _list.ItemsSource = rows;
        _list.FontFamily = new FontFamily("Consolas");
        if (rows.Count > 0) _list.SelectedIndex = 0;
    }

    private void Accept()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _shown.Count) return;

        SensorDescriptor d = _shown[i];
        string fmt = _format.Text.Trim();

        // A text source has nothing to format, and "{time.now:0}" would only
        // look like it does something.
        Token = d.IsText || fmt.Length == 0 ? $"{{{d.Id}}}" : $"{{{d.Id}:{fmt}}}";

        DialogResult = true;
        Close();
    }
}

/// <summary>A one-line text prompt, for naming and renaming profiles.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box = new();

    public string Value => _box.Text.Trim();

    public PromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush?)TryFindResource("Bg") ?? Brushes.Black;

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush?)TryFindResource("TextDim") ?? Brushes.Gray,
        });

        _box.Text = initial;
        _box.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var ok = new Button
        {
            Content = "OK",
            Style = TryFindResource("BtnPrimary") as Style,
            IsDefault = true,
        };
        ok.Click += (_, _) =>
        {
            if (Value.Length == 0) return;   // an unnamed profile is not useful
            DialogResult = true;
            Close();
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Margin = new Thickness(8, 0, 0, 0),
            Style = TryFindResource("Btn") as Style,
            IsCancel = true,
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
