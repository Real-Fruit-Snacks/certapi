using System;
using System.Threading;
using System.Windows;

namespace ApiTester.Tests;

/// <summary>Constructs the StreamWindow with the real RFS theme merged, on an STA
/// thread, to prove its XAML loads (StaticResource keys resolve, markup is well-formed) — the
/// failure mode a plain build can't catch, since StaticResource resolution happens at load time.</summary>
public class StreamWindowSmokeTests
{
    [Fact]
    public void StreamWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                var win = new ApiTester.App.StreamWindow("wss://example.test/socket", null, insecure: false);
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void OAuthWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                var win = new ApiTester.App.OAuthWindow(null, insecure: false, "https://api.example.com/orders");
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void TrustedCertsWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                // Seeded with a pin so the populated branch of the list is exercised too, not just
                // the empty state.
                var state = new ApiTester.Core.AppState();
                ApiTester.Core.TrustService.Trust(
                    state, "internal.corp", "A8448F8E50F06E51750B0AAD31A960B84B04D03B", "CN=internal.corp");

                var win = new ApiTester.App.TrustedCertsWindow(state);
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void ChainEditorWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                // Seeded with a real saved request inside a folder (so the depth-first flatten runs)
                // and a chain whose second step points at an id that no longer resolves — the
                // "(missing request)" branch is the one most likely to throw at load, so it must be
                // exercised here rather than only the empty state.
                var state = new ApiTester.Core.AppState();
                var saved = new ApiTester.Core.CollectionNode
                {
                    Name = "Login",
                    IsFolder = false,
                    Request = new ApiTester.Core.RequestModel { Method = "POST", Path = "/login" }
                };
                state.Collections.Add(new ApiTester.Core.CollectionNode
                {
                    Name = "Auth",
                    IsFolder = true,
                    Children = { saved }
                });

                var chain = new ApiTester.Core.RequestChain { Name = "login then list", EnvironmentName = "chain" };
                chain.Steps.Add(new ApiTester.Core.ChainStep { RequestId = saved.Id });
                chain.Steps.Add(new ApiTester.Core.ChainStep { RequestId = "deleted-request-id", StopOnFailure = false });
                state.Chains.Add(chain);

                var win = new ApiTester.App.ChainEditorWindow(chain, state);
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void ChainRunWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                // Seeded with a real saved request nested inside a folder (so the depth-first walk
                // actually recurses) and a chain whose second step points at an id that no longer
                // resolves — the "(missing request)" row is the branch most likely to throw at load,
                // so it must be exercised here rather than only the empty state.
                var state = new ApiTester.Core.AppState();
                var saved = new ApiTester.Core.CollectionNode
                {
                    Name = "Login",
                    IsFolder = false,
                    Request = new ApiTester.Core.RequestModel { Method = "POST", Path = "/login" }
                };
                state.Collections.Add(new ApiTester.Core.CollectionNode
                {
                    Name = "Auth",
                    IsFolder = true,
                    Children = { saved }
                });

                var chain = new ApiTester.Core.RequestChain { Name = "login then list", EnvironmentName = "chain" };
                chain.Steps.Add(new ApiTester.Core.ChainStep { RequestId = saved.Id });
                chain.Steps.Add(new ApiTester.Core.ChainStep { RequestId = "deleted-request-id", StopOnFailure = false });
                state.Chains.Add(chain);

                var win = new ApiTester.App.ChainRunWindow(
                    state, new ApiTester.Core.ApiClient(), new ApiTester.Core.TrustPredicates(state),
                    chain, _ => null);
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void MockServerWindow_loads_with_the_theme()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/ApiTester.App;component/Themes/TerminalWorkbench.xaml")
                });

                var win = new ApiTester.App.MockServerWindow();
                win.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        Assert.True(error is null, error?.ToString());
    }
}
