namespace AppSentry.ViewModels;

/// <summary>
/// A button an edition adds for a change: in its details pane and on its notification. <see cref="Id"/> names it in
/// the notification's arguments; <see cref="Run"/> does it (on the UI thread, with the window open).
/// </summary>
public sealed record EventAction(string Id, string Text, string Glyph, Func<Task> Run);
