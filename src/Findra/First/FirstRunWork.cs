using System;
using System.Collections.Generic;
using System.Linq;

using SkiaSharp;

namespace Findra;

/// <summary>Where one step of the work behind a welcome-screen button is.</summary>
public enum StepState { Todo, Now, Done }

public readonly record struct WorkStep(string Label, StepState State);

/// <summary>
/// What the welcome screen shows while Findra does the work behind a button: a title and the
/// steps, each ticked as it really finishes.
///
/// <para>Without it the window sat still while the shell started the hotkey, the capsule, the index
/// and the name helper on the interface thread, and then jumped to the next page - which read as a
/// click that did nothing and then a screen that changed by itself. The shell ticks the steps
/// (<see cref="Done"/>) and yields between them, so each tick is drawn.</para>
///
/// <para>Steps can finish out of order: name search waits for the Windows prompt while the hotkey
/// and capsule are made. The spinner stays on the first step not yet done.</para>
/// </summary>
public sealed record FirstRunWork(string Title, IReadOnlyList<WorkStep> Steps)
{
    public const string PermissionStep = "Starting name search (Windows may ask for permission)";
    public const int SavingStep = 0, NameSearchStep = 1, HotkeyStep = 2, DownloadStep = 3;

    /// <summary>After "Get these" or "Not now". The download step only when something is fetched.</summary>
    public static FirstRunWork SettingUp(bool downloading) => Start("Setting up Findra",
    [
        "Saving your choices",
        PermissionStep,
        "Getting the hotkey and the capsule ready",
        .. downloading ? new[] { "Starting the download" } : [],
    ]);

    /// <summary>After the answered page's closing buttons.</summary>
    public static FirstRunWork Closing(bool startReading) => Start("Almost done",
    [
        "Saving your answer",
        .. startReading ? new[] { "Starting to read your files" } : [],
    ]);

    private static FirstRunWork Start(string title, IReadOnlyList<string> labels) =>
        new(title, [.. labels.Select((l, i) => new WorkStep(l, i == 0 ? StepState.Now : StepState.Todo))]);

    /// <summary>Step <paramref name="i"/> is finished; the first step still to do is under way.
    /// A step that is not there changes nothing.</summary>
    public FirstRunWork Done(int i)
    {
        if (i < 0 || i >= Steps.Count) return this;
        var steps = Steps.Select((s, j) => j == i ? s with { State = StepState.Done } : s).ToList();
        int now = steps.FindIndex(s => s.State != StepState.Done);
        for (int j = 0; j < steps.Count; j++)
            if (steps[j].State != StepState.Done) steps[j] = steps[j] with { State = j == now ? StepState.Now : StepState.Todo };
        return this with { Steps = steps };
    }

    public bool Finished => Steps.All(s => s.State == StepState.Done);

    // Records compare lists by reference; two works with the same steps are the same work.
    public bool Equals(FirstRunWork? other) =>
        other is not null && Title == other.Title && Steps.SequenceEqual(other.Steps);

    public override int GetHashCode() => HashCode.Combine(Title, Steps.Count);
}

/// <summary>The card, drawn over whichever page is up, on the terms the settings window's panels
/// use: a scrim so the page is plainly not what is being answered, and the card in the middle.
/// </summary>
public static class FirstRunWorkPainter
{
    public const float Width = 440f;
    public const float Pad = 24f;
    public const float TitleSize = 17f;
    public const float StepH = 30f;

    public static void Paint(SKCanvas canvas, FirstRunWork work, float surfaceHeight, float spin, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(d);

        using (var scrim = new SKPaint { Color = d.Ground.WithAlpha(196), IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, FirstRunLayout.Width, surfaceHeight), FirstRunLayout.Radius), scrim);

        float h = Pad + TitleSize + 16f + work.Steps.Count * StepH + Pad - 6f;
        float left = (FirstRunLayout.Width - Width) / 2f, top = (surfaceHeight - h) / 2f;
        var panel = new SKRect(left, top, left + Width, top + h);
        using (var fill = new SKPaint { Color = d.Tile, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(panel, 14f), fill);
        using (var edge = new SKPaint
        { Color = d.Accent.WithAlpha(96), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(new SKRoundRect(panel, 14f), edge);

        float x = left + Pad;
        CardText.Draw(canvas, work.Title, x, top + Pad + TitleSize, TitleSize, face, d.Ink);

        float y = top + Pad + TitleSize + 16f;
        foreach (WorkStep step in work.Steps)
        {
            float cy = y + StepH / 2f - 2f;
            Icon(canvas, step.State, x + 8f, cy, spin, d);
            CardText.Draw(canvas, CardText.Ellipsize(step.Label, face, Parts.LabelSize, Width - 2 * Pad - 28f),
                          x + 28f, cy + 4.5f, Parts.LabelSize, face,
                          step.State == StepState.Todo ? d.Fade(140) : step.State == StepState.Now ? d.Ink : d.Fade(200));
            y += StepH;
        }
    }

    private static void Icon(SKCanvas canvas, StepState state, float cx, float cy, float spin, Derived d)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
        switch (state)
        {
            case StepState.Done:
                using (var disc = new SKPaint { Color = d.Accent.WithAlpha(46), IsAntialias = true })
                    canvas.DrawCircle(cx, cy, 8f, disc);
                p.Color = d.Accent;
                p.StrokeWidth = 2f;
                canvas.DrawLine(cx - 3.5f, cy, cx - 1f, cy + 3f, p);
                canvas.DrawLine(cx - 1f, cy + 3f, cx + 4f, cy - 3f, p);
                break;
            case StepState.Now:
                p.StrokeWidth = 2.2f;
                p.Color = d.Accent.WithAlpha(60);
                canvas.DrawCircle(cx, cy, 7f, p);
                p.Color = d.Accent;
                using (var arc = new SKPath())
                {
                    arc.AddArc(new SKRect(cx - 7f, cy - 7f, cx + 7f, cy + 7f), spin * 360f - 90f, 100f);
                    canvas.DrawPath(arc, p);
                }
                break;
            case StepState.Todo:
                p.StrokeWidth = 1.4f;
                p.Color = d.Fade(90);
                canvas.DrawCircle(cx, cy, 6.5f, p);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "no icon for this step state");
        }
    }
}
