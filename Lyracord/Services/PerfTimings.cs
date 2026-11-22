using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Speed measurements for the report: the steps of starting the app (counted from the moment the process
    /// started), the gateway timings from DSharpPlus and how long pages take to build. No content, no tokens.
    /// </summary>
    internal static class PerfTimings
    {
        private static readonly object _lock = new object();
        private static readonly List<string> _startup = new List<string>();
        private static readonly List<string> _recent = new List<string>();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double _offsetMs = -1;
        private static bool _startupDone;

        /// <summary>Time from the process start to the first call (the App constructor), measured once.</summary>
        public static void Init()
        {
            if (_offsetMs >= 0)
                return;
            _offsetMs = 0;
            try
            {
                var info = Windows.System.Diagnostics.ProcessDiagnosticInfo.GetForCurrentProcess();
                var sinceStart = DateTimeOffset.Now - info.ProcessStartTime;
                if (sinceStart > TimeSpan.Zero && sinceStart < TimeSpan.FromMinutes(5))
                    _offsetMs = sinceStart.TotalMilliseconds - _clock.Elapsed.TotalMilliseconds;
            }
            catch (Exception)
            {
                // older systems: count from the App constructor
            }
            Mark("запуск приложения (App)");
        }

        private static double Now => _offsetMs + _clock.Elapsed.TotalMilliseconds;

        /// <summary>A step of starting the app, shown with the time since the process started.</summary>
        public static void Mark(string step)
        {
            if (_offsetMs < 0)
                Init();
            var line = $"{Now / 1000.0,6:0.00} с  {step}";
            lock (_lock)
            {
                if (_startupDone || _startup.Count > 40)
                    return;
                _startup.Add(line);
            }
            Log("Замер: " + line.Trim());
        }

        private static bool _hubShown, _readyDone;

        /// <summary>The hub is on screen. Start-up ends once the hub is shown and READY has been handled.</summary>
        public static void StartupFinished(string step)
        {
            Mark(step);
            lock (_lock)
            {
                _hubShown = true;
                _startupDone = _hubShown && _readyDone;
            }
        }

        /// <summary>A measurement after start-up (opening a chat, building a page); the latest 30 are kept.</summary>
        public static void Add(string what, double ms)
        {
            bool startup;
            lock (_lock)
                startup = !_startupDone;
            if (startup)
            {
                Mark($"{what}: {ms:0} мс"); // part of starting up: keep it in order with the other steps
                return;
            }

            var line = $"{DateTime.Now:HH:mm:ss}  {what}: {ms:0} мс";
            lock (_lock)
            {
                _recent.Add(line);
                if (_recent.Count > 30)
                    _recent.RemoveAt(0);
            }
            Log("Замер: " + what + $": {ms:0} мс");
        }

        /// <summary>Measures the time until the returned object is disposed.</summary>
        public static Measurement Measure(string what) => new Measurement(what);

        /// <summary>A gateway timing line from DSharpPlus (READY size, parsing, slow events).</summary>
        public static void Gateway(string line)
        {
            bool startup;
            lock (_lock)
                startup = !_startupDone;
            if (startup)
                Mark("шлюз · " + line);
            else
                Log("Замер: шлюз · " + line); // also listed under "Шлюз Discord" in the report

            if (line.StartsWith("READY:", StringComparison.Ordinal))
            {
                lock (_lock)
                {
                    _readyDone = true;
                    _startupDone = _hubShown && _readyDone;
                }
            }
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            lock (_lock)
            {
                sb.AppendLine("Запуск (время от старта процесса):");
                if (_startup.Count == 0)
                    sb.AppendLine("  (нет данных)");
                foreach (var line in _startup)
                    sb.AppendLine("  " + line);
                if (!_startupDone)
                    sb.AppendLine(_hubShown ? "  (данные Discord ещё не получены)" : "  (хаб ещё не показан)");

                sb.AppendLine("Страницы и чаты:");
                if (_recent.Count == 0)
                    sb.AppendLine("  (пока ничего не открывали)");
                foreach (var line in _recent)
                    sb.AppendLine("  " + line);
            }

            try
            {
                var gateway = DiscordManager.Discord?.Diagnostics;
                if (gateway != null && gateway.Length > 0)
                {
                    sb.AppendLine("Шлюз Discord (большие и медленные события):");
                    foreach (var line in gateway)
                        sb.AppendLine("  " + line);
                }
            }
            catch (Exception) { }

            try
            {
                sb.AppendLine($"Память: {Windows.System.MemoryManager.AppMemoryUsage / 1048576.0:0} МБ из {Windows.System.MemoryManager.AppMemoryUsageLimit / 1048576.0:0} МБ");
                sb.AppendLine($"Сборка клиента Discord: {DSharpPlus.DiscordClient.ClientBuildNumber}" +
                              (DSharpPlus.DiscordClient.ClientBuildNumber == DSharpPlus.DiscordClient.DefaultClientBuildNumber ? " (встроенная)" : " (задана в настройках)"));
            }
            catch (Exception) { }
            return sb.ToString();
        }

        private static void Log(string line)
        {
            try { VoiceDiagnostics.Log(line); } catch (Exception) { }
        }

        internal sealed class Measurement : IDisposable
        {
            private readonly string _what;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private bool _done;

            public Measurement(string what) => _what = what;

            public void Dispose()
            {
                if (_done)
                    return;
                _done = true;
                Add(_what, _watch.Elapsed.TotalMilliseconds);
            }
        }
    }
}
