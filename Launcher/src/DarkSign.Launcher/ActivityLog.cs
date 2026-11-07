using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DarkSign.Launcher;

/// <summary>
/// Everything the launcher does, with the time: status messages, the commands it runs, and every line the porter
/// prints. Shown in the "Launcher activity" drop-down and written to TheDarkSignLauncher.log next to the exe (the
/// "Save test report" zip already picks up *.log from the launcher folder). Write is safe from any thread; the view is
/// refreshed in batches so a busy build doesn't slow the window down.
/// </summary>
public sealed class ActivityLog : IDisposable
{
    public const string FileName = "TheDarkSignLauncher.log";

    /// <summary>A log bigger than this at start is moved to TheDarkSignLauncher.old.log.</summary>
    private const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Lines kept in the window (the file keeps everything).</summary>
    private const int MaxViewLines = 4000;

    private readonly TextBox _view;
    private readonly CheckBox _follow;
    private readonly object _lock = new();
    private readonly List<string> _pending = [];
    private readonly Queue<int> _lineLengths = new();
    private readonly DispatcherTimer _flush;
    private StreamWriter? _file;

    public string Path { get; }

    public ActivityLog(TextBox view, CheckBox follow, string path)
    {
        _view = view;
        _follow = follow;
        Path = path;
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                File.Move(path, System.IO.Path.ChangeExtension(path, ".old.log"), overwrite: true);
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _file.WriteLine();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only launcher folder: the drop-down still works.
            _file = null;
        }
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Flush(), view.Dispatcher);
        _flush.Start();
    }

    public void Write(string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        lock (_lock)
        {
            foreach (var line in message.Replace("\r", "").Split('\n'))
            {
                var text = $"{stamp}  {line}";
                _pending.Add(text);
                try { _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd} {text}"); }
                catch (IOException) { _file = null; }
            }
        }
    }

    public void ClearView()
    {
        _view.Clear();
        _lineLengths.Clear();
    }

    private void Flush()
    {
        string[] lines;
        lock (_lock)
        {
            if (_pending.Count == 0)
                return;
            lines = [.. _pending];
            _pending.Clear();
        }
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
            _lineLengths.Enqueue(line.Length + 1);
        }
        _view.AppendText(sb.ToString());
        if (_lineLengths.Count > MaxViewLines + 500)
        {
            var cut = 0;
            while (_lineLengths.Count > MaxViewLines)
                cut += _lineLengths.Dequeue();
            _view.Text = _view.Text[Math.Min(cut, _view.Text.Length)..];
        }
        if (_follow.IsChecked == true)
            _view.ScrollToEnd();
    }

    public void Dispose()
    {
        _flush.Stop();
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
