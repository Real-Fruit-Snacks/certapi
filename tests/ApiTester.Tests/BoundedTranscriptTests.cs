using System;
using System.Threading;
using System.Windows.Controls;
using ApiTester.App;

namespace ApiTester.Tests;

/// <summary>The stream and mock-server windows keep their logs to a bounded size, so a long session
/// or one huge message cannot grow the TextBox until the window stops responding.</summary>
public class BoundedTranscriptTests
{
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.True(error is null, error?.ToString());
    }

    [Fact]
    public void A_long_session_keeps_only_its_latest_lines() => OnSta(() =>
    {
        var box = new TextBox();
        var log = new BoundedTranscript(box);
        string filler = new string('.', 1000);
        for (int i = 0; i < 2000; i++) log.Append($"{i:D4} {filler}");

        Assert.True(box.Text.Length <= BoundedTranscript.MaxChars, $"the log grew to {box.Text.Length:N0} characters");
        Assert.StartsWith("… earlier lines removed", box.Text);
        Assert.Contains("1999 ", box.Text);
        Assert.DoesNotContain("0000 ", box.Text);
    });

    [Fact]
    public void One_huge_message_is_cut() => OnSta(() =>
    {
        var box = new TextBox();
        var log = new BoundedTranscript(box);
        log.Append(new string('m', BoundedTranscript.MaxLineChars + 500));

        Assert.Contains("(500 more characters not shown)", box.Text);
        Assert.True(box.Text.Length < BoundedTranscript.MaxLineChars + 100);

        log.Clear();
        Assert.Equal("", box.Text);
    });
}
