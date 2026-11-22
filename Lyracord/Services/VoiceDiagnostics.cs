using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Voice troubleshooting for Windows 10 Mobile: an event log of the last voice connection,
    /// a device report and a microphone/speaker test that bypasses the native voice module.
    /// Contains no tokens or message content, so the report is safe to share.
    /// </summary>
    internal static class VoiceDiagnostics
    {
        private static readonly List<string> _log = new List<string>();
        private static readonly object _lock = new object();

        private const string AppLogName = "voice-app.log";
        private const long AppLogLimit = 256 * 1024;
        private static string _appLogPath;

        // Lines wait here and reach the disk from a background thread about once a second: opening and appending
        // to the file on every call cost the UI thread several milliseconds each on the phone.
        private static readonly StringBuilder _pending = new StringBuilder();
        private static bool _flushScheduled;
        private static readonly object _fileLock = new object();

        public static void Log(string message)
        {
            var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            lock (_lock)
            {
                _log.Add(line);
                if (_log.Count > 300) _log.RemoveAt(0);
                _pending.Append(DateTime.Now.ToString("dd.MM ")).Append(line).Append("\r\n");
                if (_flushScheduled)
                    return;
                _flushScheduled = true;
            }
            Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);
                Flush();
            });
        }

        /// <summary>
        /// Writes the waiting lines to LocalState\voice-app.log so the cause of a crash survives the restart
        /// (the in-memory log is lost with the process). Called on a timer, before reading the file and on a crash.
        /// </summary>
        public static void Flush()
        {
            string text;
            lock (_lock)
            {
                _flushScheduled = false;
                if (_pending.Length == 0)
                    return;
                text = _pending.ToString();
                _pending.Clear();
            }

            lock (_fileLock)
            {
                try
                {
                    if (_appLogPath == null)
                        _appLogPath = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, AppLogName);

                    var info = new System.IO.FileInfo(_appLogPath);
                    if (info.Exists && info.Length > AppLogLimit)
                    {
                        // keep the newer half
                        var old = System.IO.File.ReadAllText(_appLogPath, Encoding.UTF8);
                        var cut = old.IndexOf('\n', old.Length / 2);
                        System.IO.File.WriteAllText(_appLogPath, cut >= 0 ? old.Substring(cut + 1) : "", Encoding.UTF8);
                    }
                    System.IO.File.AppendAllText(_appLogPath, text, Encoding.UTF8);
                }
                catch (Exception)
                {
                    // logging must never take the app down
                }
            }
        }

        /// <summary>Unhandled error with everything needed to find it: type, HRESULT, message, stack, inner exceptions.</summary>
        public static void LogCrash(string source, Exception ex)
        {
            if (ex == null)
            {
                Log($"НЕОБРАБОТАННАЯ ОШИБКА ({source}): нет объекта исключения");
                return;
            }
            var sb = new StringBuilder();
            sb.Append($"НЕОБРАБОТАННАЯ ОШИБКА ({source}) 0x{ex.HResult:X8} {ex.GetType().FullName}: {ex.Message}");
            var inner = ex.InnerException;
            for (int i = 0; inner != null && i < 5; i++, inner = inner.InnerException)
                sb.Append($"\n   внутри: 0x{inner.HResult:X8} {inner.GetType().FullName}: {inner.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
                sb.Append("\n" + ex.StackTrace);
            Log(sb.ToString());
            Flush(); // the process may be about to end
        }

        /// <summary>Tail of LocalState\voice-app.log: the app's own log across restarts, including crashes.</summary>
        public static string AppLogTail(int maxLines = 150) => FileTail(AppLogName, maxLines, "(журнал приложения пуст)");

        private static string FileTail(string name, int maxLines, string missing)
        {
            if (name == AppLogName)
                Flush();
            try
            {
                var path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, name);
                if (!System.IO.File.Exists(path))
                    return missing;

                var lines = new List<string>();
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                        if (lines.Count > maxLines) lines.RemoveAt(0);
                    }
                }
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return $"{name}: ошибка 0x{ex.HResult:X8} {ex.Message}";
            }
        }

        public static void Log(string step, Exception ex)
        {
            Log($"{step}: ОШИБКА 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
        }

        public static void Clear()
        {
            lock (_lock) _log.Clear();
        }

        public static string LogText
        {
            get
            {
                lock (_lock) return _log.Count == 0 ? "(подключений к голосу ещё не было)" : string.Join("\n", _log);
            }
        }

        /// <summary>
        /// Tail of the native voice module's log (LocalState\voice-native.log). The module never
        /// writes the voice token or encryption keys there.
        /// </summary>
        public static string NativeLogTail(int maxLines = 200)
        {
            try
            {
                var path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "voice-native.log");
                if (!System.IO.File.Exists(path))
                    return "(журнала модуля ещё нет)";

                var lines = new List<string>();
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                        if (lines.Count > maxLines) lines.RemoveAt(0);
                    }
                }
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return $"журнал модуля: ошибка 0x{ex.HResult:X8} {ex.Message}";
            }
        }

        /// <summary>Tail of LocalState\stream-events.log (Go Live: gateway events with tokens redacted, player stats).</summary>
        public static string StreamLogTail(int maxLines = 60)
        {
            Models.Voice.StreamWatchModel.FlushStreamLog();
            try
            {
                var path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "stream-events.log");
                if (!System.IO.File.Exists(path))
                    return "(трансляции ещё не открывались)";

                var lines = new List<string>();
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                        if (lines.Count > maxLines) lines.RemoveAt(0);
                    }
                }
                return string.Join("\n", lines);
            }
            catch (Exception ex)
            {
                return $"журнал трансляций: ошибка 0x{ex.HResult:X8} {ex.Message}";
            }
        }

        /// <summary>The whole diagnostics report ("Копировать отчёт"), shared by voice settings and the Metro settings list.</summary>
        public static async Task<string> BuildReportAsync(string tests = null)
        {
            var devices = await DeviceReportAsync();
            string speed;
            try { speed = PerfTimings.Report(); } catch (Exception ex) { speed = "ошибка: " + ex.Message; }
            return devices + "\n== Скорость\n" + speed + "\n== Тесты микрофона\n" + (string.IsNullOrEmpty(tests) ? "(тесты не запускались)" : tests) +
                   "\n\n== Журнал последнего подключения к голосу\n" + LogText +
                   "\n\n== Журнал приложения (в файле, переживает вылеты)\n" + AppLogTail() +
                   "\n\n== Журнал трансляций\n" + StreamLogTail() +
                   "\n\n== Журнал голосового модуля (последние строки)\n" + NativeLogTail();
        }

        public static string CurrentRouting()
        {
            if (!ApiInformation.IsTypePresent("Windows.Phone.Media.Devices.AudioRoutingManager"))
                return "AudioRoutingManager недоступен";
            try
            {
                var routing = Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault();
                return $"маршрут={routing.GetAudioEndpoint()}, доступно={routing.AvailableAudioEndpoints}";
            }
            catch (Exception ex)
            {
                return $"маршрут: ошибка 0x{ex.HResult:X8} {ex.Message}";
            }
        }

        public static async Task<string> DeviceReportAsync()
        {
            var sb = new StringBuilder();
            sb.AppendLine("== Устройство");
            try
            {
                var info = new Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation();
                var v = ulong.Parse(Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion);
                sb.AppendLine($"{info.SystemManufacturer} {info.SystemProductName}, {Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily} " +
                              $"{v >> 48}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}");
            }
            catch (Exception ex) { sb.AppendLine("ошибка: " + ex.Message); }

            sb.AppendLine("== Устройства по умолчанию");
            await AppendDefaultAsync(sb, "Микрофон (обычный)", () => MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default));
            await AppendDefaultAsync(sb, "Микрофон (звонки)", () => MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Communications));
            await AppendDefaultAsync(sb, "Динамик (обычный)", () => MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default));
            await AppendDefaultAsync(sb, "Динамик (звонки)", () => MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Communications));

            sb.AppendLine("== Все микрофоны");
            await AppendListAsync(sb, MediaDevice.GetAudioCaptureSelector());
            sb.AppendLine("== Все динамики");
            await AppendListAsync(sb, MediaDevice.GetAudioRenderSelector());

            sb.AppendLine("== Маршрут звука сейчас");
            sb.AppendLine(CurrentRouting());

            sb.AppendLine("== Настройки голоса");
            sb.AppendLine($"InputDevice={Short(App.LocalSettings.Read<string>("InputDevice", null))}, OutputDevice={Short(App.LocalSettings.Read<string>("OutputDevice", null))}");
            sb.AppendLine($"PhoneCallMode={App.LocalSettings.Read("PhoneCallMode", true)}, VoiceActivity={App.LocalSettings.Read("VoiceActivity", true)}, " +
                          $"NoiseSuppression={App.LocalSettings.Read("NoiseSuppression", 2u)}, AGC={App.LocalSettings.Read("AutomaticGainControl", true)}, " +
                          $"EchoCancellation={App.LocalSettings.Read("EchoCancellation", true)}");
            return sb.ToString();
        }

        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(по умолчанию)";
            return id.Length > 60 ? "…" + id.Substring(id.Length - 60) : id;
        }

        private static async Task AppendDefaultAsync(StringBuilder sb, string label, Func<string> getId)
        {
            try
            {
                var id = getId();
                if (string.IsNullOrEmpty(id))
                {
                    sb.AppendLine($"{label}: нет");
                    return;
                }
                var info = await DeviceInformation.CreateFromIdAsync(id);
                sb.AppendLine($"{label}: {info.Name}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{label}: ошибка 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        private static async Task AppendListAsync(StringBuilder sb, string selector)
        {
            try
            {
                var all = await DeviceInformation.FindAllAsync(selector);
                foreach (var d in all) sb.AppendLine($"- {d.Name}{(d.IsEnabled ? "" : " (выключено)")}");
                if (all.Count == 0) sb.AppendLine("- нет");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ошибка 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        /// <summary>
        /// Records a few seconds from the microphone and plays it back through the speaker,
        /// without the Discord voice module. callMode wraps the test in a VoIP call and uses
        /// the Communications category, like voice does.
        /// </summary>
        public static async Task<string> LoopbackTestAsync(bool callMode, int seconds = 4)
        {
            var sb = new StringBuilder();
            sb.AppendLine(callMode ? "== Тест: режим звонка (Communications + VoIP-звонок)" : "== Тест: обычный звук (Media)");
            VoipPhoneCall call = null;
            AudioGraph graph = null;
            try
            {
                if (callMode)
                {
                    try
                    {
                        call = VoipCallCoordinator.GetDefault().RequestNewOutgoingCall(string.Empty, "Lyracord test", "Lyracord", VoipPhoneCallMedia.Audio);
                        call?.NotifyCallActive();
                        sb.AppendLine(call != null ? "звонок: активен" : "звонок: не создан (null)");
                        await Task.Delay(400);
                        if (ApiInformation.IsTypePresent("Windows.Phone.Media.Devices.AudioRoutingManager"))
                        {
                            try
                            {
                                Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault()
                                    .SetAudioEndpoint(Windows.Phone.Media.Devices.AudioRoutingEndpoint.Speakerphone);
                            }
                            catch (Exception ex) { sb.AppendLine($"громкая связь: ошибка 0x{ex.HResult:X8} {ex.Message}"); }
                        }
                        sb.AppendLine(CurrentRouting());
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"звонок: ошибка 0x{ex.HResult:X8} {ex.Message}");
                    }
                }

                var settings = new AudioGraphSettings(callMode ? AudioRenderCategory.Communications : AudioRenderCategory.Media);
                var graphResult = await AudioGraph.CreateAsync(settings);
                if (graphResult.Status != AudioGraphCreationStatus.Success)
                {
                    sb.AppendLine($"AudioGraph: {graphResult.Status} 0x{graphResult.ExtendedError?.HResult:X8}");
                    return sb.ToString();
                }
                graph = graphResult.Graph;
                sb.AppendLine($"AudioGraph: {graph.EncodingProperties.SampleRate} Гц, {graph.EncodingProperties.ChannelCount} кан.");

                var output = await graph.CreateDeviceOutputNodeAsync();
                sb.AppendLine($"динамик: {output.Status}{(output.DeviceOutputNode != null ? " → " + output.DeviceOutputNode.Device?.Name : "")}");
                var input = await graph.CreateDeviceInputNodeAsync(callMode ? MediaCategory.Communications : MediaCategory.Other);
                sb.AppendLine($"микрофон: {input.Status}{(input.DeviceInputNode != null ? " → " + input.DeviceInputNode.Device?.Name : "")}");
                if (output.Status != AudioDeviceNodeCreationStatus.Success || input.Status != AudioDeviceNodeCreationStatus.Success)
                    return sb.ToString();

                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync("voice-test.wav", CreationCollisionOption.ReplaceExisting);
                var fileOut = await graph.CreateFileOutputNodeAsync(file, MediaEncodingProfile.CreateWav(AudioEncodingQuality.Medium));
                if (fileOut.Status != AudioFileNodeCreationStatus.Success)
                {
                    sb.AppendLine($"файл записи: {fileOut.Status}");
                    return sb.ToString();
                }
                input.DeviceInputNode.AddOutgoingConnection(fileOut.FileOutputNode);
                graph.Start();
                await Task.Delay(seconds * 1000);
                graph.Stop();
                await fileOut.FileOutputNode.FinalizeAsync();
                input.DeviceInputNode.RemoveOutgoingConnection(fileOut.FileOutputNode);

                var bytes = (await FileIO.ReadBufferAsync(file)).ToArray();
                sb.AppendLine($"запись: {bytes.Length} байт, пиковая громкость {PeakPercent(bytes)}%");

                var fileIn = await graph.CreateFileInputNodeAsync(file);
                if (fileIn.Status != AudioFileNodeCreationStatus.Success)
                {
                    sb.AppendLine($"воспроизведение файла: {fileIn.Status}");
                    return sb.ToString();
                }
                fileIn.FileInputNode.AddOutgoingConnection(output.DeviceOutputNode);
                graph.Start();
                await Task.Delay(seconds * 1000 + 500);
                graph.Stop();
                sb.AppendLine("воспроизведение: выполнено (слышно ли было запись — отметьте сами)");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"ошибка 0x{ex.HResult:X8} {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { graph?.Dispose(); } catch { }
                try { call?.NotifyCallEnded(); } catch { }
            }
            return sb.ToString();
        }

        // Peak amplitude of a PCM WAV (16-bit integer or 32-bit float) as a percentage of full scale.
        private static int PeakPercent(byte[] wav)
        {
            try
            {
                int bits = 16;
                bool isFloat = false;
                int pos = 12;
                while (pos + 8 <= wav.Length)
                {
                    var id = Encoding.ASCII.GetString(wav, pos, 4);
                    var size = BitConverter.ToInt32(wav, pos + 4);
                    if (id == "fmt ")
                    {
                        isFloat = BitConverter.ToInt16(wav, pos + 8) == 3;
                        bits = BitConverter.ToInt16(wav, pos + 22);
                    }
                    else if (id == "data")
                    {
                        double peak = 0;
                        int end = Math.Min(wav.Length, pos + 8 + size);
                        if (isFloat && bits == 32)
                        {
                            for (int i = pos + 8; i + 4 <= end; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(wav, i)));
                        }
                        else if (bits == 16)
                        {
                            for (int i = pos + 8; i + 2 <= end; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(wav, i) / 32768.0));
                        }
                        return (int)Math.Round(Math.Min(1, peak) * 100);
                    }
                    pos += 8 + size + (size & 1);
                }
            }
            catch (Exception)
            {
            }
            return -1;
        }
    }
}
