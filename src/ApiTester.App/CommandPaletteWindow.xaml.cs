using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApiTester.App;

public sealed record PaletteItem(string Category, string Title, string Subtitle, Action Execute);

public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteItem> _all;
    public PaletteItem? Chosen { get; private set; }

    public CommandPaletteWindow(IReadOnlyList<PaletteItem> items)
    {
        InitializeComponent();
        _all = items;
        ResultsList.ItemsSource = _all;
        if (_all.Count > 0) ResultsList.SelectedIndex = 0;
        Loaded += (_, _) => QueryBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = (QueryBox.Text ?? "").Trim();
        if (q.Length == 0)
        {
            ResultsList.ItemsSource = _all;
        }
        else
        {
            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ResultsList.ItemsSource = _all.Where(item =>
            {
                foreach (var t in terms)
                {
                    if (!item.Title.Contains(t, StringComparison.OrdinalIgnoreCase) &&
                        !item.Subtitle.Contains(t, StringComparison.OrdinalIgnoreCase) &&
                        !item.Category.Contains(t, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }).ToList();
        }
        if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = (ResultsList.SelectedIndex + 1) % ResultsList.Items.Count;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && ResultsList.Items.Count > 0)
        {
            ResultsList.SelectedIndex = (ResultsList.SelectedIndex - 1 + ResultsList.Items.Count) % ResultsList.Items.Count;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            AcceptSelection();
            e.Handled = true;
        }
    }

    private void ResultsList_DoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (ResultsList.SelectedItem is not PaletteItem item) return;
        Chosen = item;
        DialogResult = true;
        Close();
    }
}
