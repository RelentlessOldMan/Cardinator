namespace Cardinator.Services;

/// <summary>
/// Progress lines for a command, written as they happen. <see cref="Progress{T}"/> made on the UI thread posts each
/// line to the dispatcher, which a command blocked on its work doesn't run until it ends — so the progress came out
/// after the summary, and a script reading the last line got "Looking up 1 card(s)…" instead of the result.
/// </summary>
public sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
