using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;

namespace BattleNetPrefill.Api;

/// <summary>
/// An IAnsiConsole adapter that routes the upstream prefill handlers' console output
/// through the <see cref="IPrefillProgress"/> interface. This allows TactProductHandler
/// and CdnRequestManager to run headless (no TTY) while still surfacing their log lines
/// as structured progress events over the socket. Battle.net is anonymous, so there is
/// no interactive credential prompting — input is always a no-op.
/// </summary>
internal sealed class ApiConsoleAdapter : IAnsiConsole
{
    private readonly IPrefillProgress _progress;

    public ApiConsoleAdapter(IPrefillProgress progress)
    {
        _progress = progress;

        Profile = new Spectre.Console.Profile(new NullConsoleOutput(), Encoding.UTF8);
    }

    public Spectre.Console.Profile Profile { get; }
    public IAnsiConsoleCursor Cursor => new NullCursor();
    public IAnsiConsoleInput Input => new ApiConsoleInput();
    public IExclusivityMode ExclusivityMode => new NoExclusivityMode();
    public RenderPipeline Pipeline => new();

    public void Clear(bool home)
    {
    }

    public void Write(IRenderable renderable)
    {
        var text = ExtractText(renderable);
        if (!string.IsNullOrWhiteSpace(text))
        {
            _progress.OnLog(LogLevel.Info, text);
        }
    }

    private string ExtractText(IRenderable renderable)
    {
        // Render the IRenderable to plain text using a temporary Spectre console
        try
        {
            var writer = new System.IO.StringWriter();
            var settings = new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(writer),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No
            };
            var tempConsole = AnsiConsole.Create(settings);
            tempConsole.Write(renderable);
            var text = writer.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }
        catch
        {
            // Fallback: try ToString but filter out Spectre type names
            var text = renderable.ToString() ?? string.Empty;
            return text.StartsWith("Spectre.Console.") ? string.Empty : text;
        }
    }

    private class NullCursor : IAnsiConsoleCursor
    {
        public void Move(CursorDirection direction, int steps) { }
        public void SetPosition(int column, int line) { }
        public void Show(bool show) { }
    }

    private class ApiConsoleInput : IAnsiConsoleInput
    {
        public bool IsKeyAvailable() => false;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            return null;
        }

        public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            return await Task.FromResult<ConsoleKeyInfo?>(null);
        }
    }

    private class NoExclusivityMode : IExclusivityMode
    {
        public T Run<T>(Func<T> func) => func();
        public async Task<T> RunAsync<T>(Func<Task<T>> func) => await func();
    }

    private class NullConsoleOutput : IAnsiConsoleOutput
    {
        public TextWriter Writer => TextWriter.Null;
        public bool IsTerminal => false;
        public int Width => 120;
        public int Height => 30;

        public void SetEncoding(Encoding encoding) { }
    }
}
