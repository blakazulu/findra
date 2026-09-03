using System;
using Avalonia.Input;

namespace Findra;

/// <summary>
/// The last step from <see cref="PointerShape"/> to the cursor Windows actually draws.
///
/// <para>Built ON DEMAND and kept, never in a static initialiser. A cursor is made through
/// Avalonia's platform factory, so constructing one before the platform is up throws - and a type
/// initialiser that throws surfaces from wherever the type is first touched, which here would be
/// the middle of a pointer move. The same reasoning <see cref="Parts.Face"/> is written under: a
/// surface that cannot get its cursor shows the arrow and logs a line, and never fails to
/// paint.</para>
/// </summary>
public static class PointerCursor
{
    private static Cursor? _hand;
    private static Cursor? _text;
    private static Cursor? _move;

    public static Cursor Of(PointerShape shape)
    {
        try
        {
            return shape switch
            {
                PointerShape.Hand => _hand ??= new Cursor(StandardCursorType.Hand),
                PointerShape.Text => _text ??= new Cursor(StandardCursorType.Ibeam),
                PointerShape.Move => _move ??= new Cursor(StandardCursorType.SizeAll),
                _ => Cursor.Default,
            };
        }
        catch (Exception ex)
        {
            Log.Once("look|cursor|" + ex.GetType().Name, "WARN", "look",
                     "the pointer cursor could not be made: " + ex.Message);
            return Cursor.Default;
        }
    }
}
