using System;
using System.Windows;
using System.Windows.Input;

namespace ApiTester.App;

/// <summary>A themed floating window that hosts a response view torn out of the main window.
/// Closing it hands the content back (see the Closed handler where it is created).</summary>
public partial class PopOutWindow : Window
{
    public PopOutWindow(string title, UIElement content)
    {
        InitializeComponent();
        Title = $"{title} — certapi";
        TitleText.Text = title.ToUpperInvariant();
        Host.Content = content;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    /// <summary>Release the hosted content so it can be re-attached to its tab.</summary>
    public UIElement? DetachContent()
    {
        var content = Host.Content as UIElement;
        Host.Content = null;
        return content;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
