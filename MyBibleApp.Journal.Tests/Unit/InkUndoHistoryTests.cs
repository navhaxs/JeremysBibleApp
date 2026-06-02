using System.Collections.Generic;
using System.Linq;
using MyBibleApp.Controls;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class InkUndoHistoryTests
{
    private static InkOverlayCanvas MakeCanvas()
    {
        return new InkOverlayCanvas { AllowMouseInput = true };
    }

    private static void DrawStroke(InkOverlayCanvas c, double x, double y)
    {
        c.StartStroke(new Avalonia.Point(x, y));
        c.ContinueStroke(new Avalonia.Point(x + 10, y + 10));
        c.EndStroke();
    }

    private static void EraseAt(InkOverlayCanvas c, double x, double y)
    {
        c.IsEraserMode = true;
        c.StartStroke(new Avalonia.Point(x, y));
        c.EndStroke();
        c.IsEraserMode = false;
    }

    [Fact]
    public void UndoStroke_AfterErase_FiresStrokeCompleted()
    {
        var canvas = MakeCanvas();
        InkStrokeEventArgs? restored = null;
        canvas.StrokeCompleted += (_, e) => restored = e;

        DrawStroke(canvas, 100, 100);
        var drawnId = restored?.StrokeId;
        restored = null;

        EraseAt(canvas, 100, 100);
        canvas.UndoStroke();

        Assert.NotNull(restored);
        Assert.Equal(drawnId, restored!.StrokeId);
    }

    [Fact]
    public void UndoStroke_AfterErase_ThenRedoStroke_FiresStrokeRemoved()
    {
        var canvas = MakeCanvas();
        InkStrokeEventArgs? completed = null;
        InkStrokeRemovedEventArgs? removed = null;
        canvas.StrokeCompleted += (_, e) => completed = e;
        canvas.StrokeRemoved += (_, e) => removed = e;

        DrawStroke(canvas, 100, 100);
        var drawnId = completed?.StrokeId;

        EraseAt(canvas, 100, 100);
        canvas.UndoStroke();  // restore
        removed = null;

        canvas.RedoStroke();  // re-erase
        Assert.NotNull(removed);
        Assert.Contains(removed!.RemovedStrokes, s => s.StrokeId == drawnId);
    }

    [Fact]
    public void UndoStroke_InterleavedDrawAndErase_UndoesInReverseOrder()
    {
        var canvas = MakeCanvas();
        var completed = new List<string>();
        var removed = new List<string>();
        canvas.StrokeCompleted += (_, e) => completed.Add(e.StrokeId);
        canvas.StrokeRemoved += (_, e) => removed.AddRange(e.RemovedStrokes.Select(s => s.StrokeId));

        // Draw A at (100, 100)
        DrawStroke(canvas, 100, 100);
        var idA = completed.Last();

        // Draw B at (300, 300) — far from eraser, won't be hit
        DrawStroke(canvas, 300, 300);
        var idB = completed.Last();

        // Erase A — eraser circle at (100,100) hits stroke A but not stroke B
        EraseAt(canvas, 100, 100);
        Assert.Single(removed);          // only one stroke erased
        Assert.Equal(idA, removed[0]);   // it was A, not B
        removed.Clear();

        // Undo erase — A restored via StrokeCompleted
        canvas.UndoStroke();
        Assert.Equal(idA, completed.Last());

        // Undo draw B — B removed via StrokeRemoved
        canvas.UndoStroke();
        Assert.Equal(idB, removed.Last());

        // Undo draw A — A removed via StrokeRemoved
        canvas.UndoStroke();
        Assert.Equal(idA, removed.Last());
    }

    [Fact]
    public void Draw_ClearsRedoHistory()
    {
        var canvas = MakeCanvas();
        var completed = new List<string>();
        canvas.StrokeCompleted += (_, e) => completed.Add(e.StrokeId);

        // Draw stroke 1, then undo it (fires StrokeRemoved, not StrokeCompleted)
        DrawStroke(canvas, 100, 100);
        canvas.UndoStroke();

        // Draw stroke 2 — clears redo history
        DrawStroke(canvas, 200, 200);

        // RedoStroke is now a no-op because the second draw cleared the redo stack
        canvas.RedoStroke();

        // completed has exactly 2 entries: stroke 1 and stroke 2
        Assert.Equal(2, completed.Count);
    }
}
