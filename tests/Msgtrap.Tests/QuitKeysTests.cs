using Msgtrap.Core;
using Xunit;

namespace Msgtrap.Tests;

public class QuitKeysTests
{
    [Fact]
    public void CtrlX_Quits()
    {
        // '\u0018' is the control code the console reports for Ctrl+X.
        var key = new ConsoleKeyInfo('\u0018', ConsoleKey.X, shift: false, alt: false, control: true);

        Assert.True(QuitKeys.IsQuit(key));
    }

    [Theory]
    [InlineData(ConsoleKey.X, false, false)]   // plain x
    [InlineData(ConsoleKey.X, true, false)]    // Alt+X
    [InlineData(ConsoleKey.C, false, true)]    // Ctrl+C is the host's job
    [InlineData(ConsoleKey.Q, false, true)]    // Ctrl+Q
    public void OtherKeys_DoNotQuit(ConsoleKey key, bool alt, bool control)
    {
        var info = new ConsoleKeyInfo('\0', key, shift: false, alt: alt, control: control);

        Assert.False(QuitKeys.IsQuit(info));
    }
}
