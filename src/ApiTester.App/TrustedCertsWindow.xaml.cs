using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Lists and removes per-site server-certificate pins (<see cref="AppState.TrustedServerCerts"/>).
/// Mirrors <see cref="EnvironmentsWindow"/>'s themed list-window shape.</summary>
public partial class TrustedCertsWindow : Window
{
    private readonly AppState _state;

    // AppState.TrustedServerCerts is a plain List<>; an ItemsControl bound to it directly would not
    // refresh on removal, so the list is folded into an ObservableCollection for display and each
    // removal is re-applied to the underlying state via TrustService.Untrust.
    private readonly ObservableCollection<TrustedServerCert> _certs;
    private bool _dirty;

    public TrustedCertsWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
        _certs = new ObservableCollection<TrustedServerCert>(_state.TrustedServerCerts);
        CertsList.ItemsSource = _certs;
        UpdateEmptyHint();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeTheme.ApplyTitleBar(this);
    }

    private void UpdateEmptyHint()
    {
        bool empty = _certs.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListBorder.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (CertsList.SelectedItem is not TrustedServerCert sel) return;
        TrustService.Untrust(_state, sel.Host, sel.Thumbprint);
        _certs.Remove(sel);
        _dirty = true;
        UpdateEmptyHint();
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty) _state.Save();
        Close();
    }
}
