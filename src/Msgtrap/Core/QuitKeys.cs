namespace Msgtrap.Core;

/// <summary>
/// Console quit keys. Ctrl+C is delivered as a host control event; Ctrl+X is not,
/// so it has to be recognised as a key press.
/// </summary>
public static class QuitKeys
{
    /// <summary>True for Ctrl+X (and not a bare <c>x</c>).</summary>
    public static bool IsQuit(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.X && key.Modifiers.HasFlag(ConsoleModifiers.Control);
}
