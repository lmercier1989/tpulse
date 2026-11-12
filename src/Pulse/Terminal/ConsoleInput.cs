namespace Pulse.Terminal;

/// <summary>A key press read from the console, with its Windows virtual-key code (on Linux decoded from the terminal's input).</summary>
internal readonly record struct KeyPress(ushort VirtualKey, char Character, bool Shift, bool Control, bool Alt);

/// <summary>A mouse event read from the console, in character cells.</summary>
/// <param name="Column">The cell column under the mouse (0-based).</param>
/// <param name="Row">The cell row under the mouse (0-based, from the top of the window).</param>
/// <param name="LeftButton">The left button is down.</param>
/// <param name="Moved">The mouse moved (rather than a button changing).</param>
/// <param name="DoubleClick">The second click of a double click.</param>
/// <param name="Wheel">Wheel steps: positive away from the user, negative toward; 0 for no wheel.</param>
/// <param name="RightButton">The right button is down.</param>
internal readonly record struct MouseInput(int Column, int Row, bool LeftButton, bool Moved, bool DoubleClick, int Wheel, bool RightButton = false);
