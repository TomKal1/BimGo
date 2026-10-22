using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WWTools.Calcs.Engine
{
    /// <summary>
    /// A timing log for calc runs. Every line is appended straight away to a rolling file
    /// (so it can be read while Revit is still busy, or after a hang), and kept in memory so the
    /// runner can also save the run's own log beside its report. Never throws.
    /// </summary>
    public sealed class CalcLog
    {
        private const long MaxBytes = 4L * 1024 * 1024;
        private readonly object _gate = new object();
        private readonly StringBuilder _run = new StringBuilder();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string? _rollingPath;

        public CalcLog(string? rollingPath)
        {
            _rollingPath = rollingPath;
        }

        /// <summary>%LocalAppData%\WWTools\Logs\&lt;name&gt;.log, e.g. Lighting.log.</summary>
        public static string RollingPath(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WWTools", "Logs", name + ".log");

        /// <summary>A log that only keeps lines in memory (tests, or when no file is wanted).</summary>
        public static CalcLog InMemory() => new CalcLog(null);

        public string Text
        {
            get { lock (_gate) return _run.ToString(); }
        }

        public void Write(string message)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} +{_clock.Elapsed.TotalSeconds,8:0.000}s  {message}";
            lock (_gate)
            {
                _run.AppendLine(line);
                if (_rollingPath == null) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_rollingPath)!);
                    if (File.Exists(_rollingPath) && new FileInfo(_rollingPath).Length > MaxBytes)
                    {
                        string old = _rollingPath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_rollingPath, old);
                    }
                    File.AppendAllText(_rollingPath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging must never disturb Revit.
                }
            }
        }

        /// <summary>Logs "label …" now and "label: N.NN s" when disposed.</summary>
        public IDisposable Time(string label)
        {
            Write(label + " …");
            return new Scope(this, label);
        }

        /// <summary>Saves this run's lines to a file (beside the report). Never throws.</summary>
        public void SaveRunTo(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Text, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly CalcLog _log;
            private readonly string _label;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private bool _done;

            public Scope(CalcLog log, string label)
            {
                _log = log;
                _label = label;
            }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _log.Write($"{_label}: {_watch.Elapsed.TotalSeconds:0.000} s");
            }
        }
    }
}
