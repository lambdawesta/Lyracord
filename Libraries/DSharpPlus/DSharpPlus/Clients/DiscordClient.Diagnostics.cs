using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DSharpPlus
{
    /// <summary>
    /// Timings of the gateway work for the app's report: how big READY was, how long unpacking, JSON parsing and
    /// filling the cache took, and every event whose handling was slow. Holds no message content or tokens.
    /// </summary>
    public sealed partial class DiscordClient
    {
        /// <summary>
        /// Build number of the official client this one presents itself as (X-Super-Properties and IDENTIFY).
        /// Discord hides newer features from old builds: with 325421 (2024) the profile returned no evolving badges
        /// (Game Time, Streaming, ... Nov 2026). Set before creating the client; the app can override it.
        /// </summary>
        public static int ClientBuildNumber { get; set; } = DefaultClientBuildNumber;

        /// <summary>Stable web client, 21 Nov 2026 (678976, e85e837).</summary>
        public const int DefaultClientBuildNumber = 678976;

        private readonly ConcurrentQueue<string> _diagnostics = new ConcurrentQueue<string>();

        /// <summary>Raised for each new timing line (also kept in <see cref="Diagnostics"/>).</summary>
        public event Action<string> DiagnosticAdded;

        /// <summary>The latest timing lines, oldest first.</summary>
        public string[] Diagnostics => this._diagnostics.ToArray();

        // filled while one gateway message is handled (messages are handled one after another)
        private long _rxCompressedBytes, _rxBytes;
        private double _rxDecompressMs, _rxParseMs;
        private string _rxEvent;

        internal static double Ms(long fromTimestamp)
            => (Stopwatch.GetTimestamp() - fromTimestamp) * 1000.0 / Stopwatch.Frequency;

        internal void AddDiagnostic(string line)
        {
            this._diagnostics.Enqueue(line);
            while (this._diagnostics.Count > 60 && this._diagnostics.TryDequeue(out _)) { }
            try { this.DiagnosticAdded?.Invoke(line); } catch (Exception) { }
        }

        private static string Size(long bytes)
            => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} МБ" : $"{bytes / 1024.0:0} КБ";

        /// <summary>
        /// What READY is made of: bytes per top-level part and per guild part (channels, emojis, roles...) summed over
        /// all guilds. Runs once, in the background a few seconds after start, on a copy of the unpacked payload.
        /// </summary>
        private static string AnalyzeReady(byte[] data)
        {
            var sizes = new System.Collections.Generic.Dictionary<string, long>();
            void Add(string key, long bytes)
            {
                sizes.TryGetValue(key, out var old);
                sizes[key] = old + bytes;
            }

            using (var text = new System.IO.StreamReader(new System.IO.MemoryStream(data), System.Text.Encoding.UTF8))
            using (var reader = new Newtonsoft.Json.JsonTextReader(text))
            {
                var info = (Newtonsoft.Json.IJsonLineInfo)reader;
                // find "d" at the root
                while (reader.Read())
                {
                    if (reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName && reader.Depth == 1 && (string)reader.Value == "d")
                        break;
                }
                if (!reader.Read() || reader.TokenType != Newtonsoft.Json.JsonToken.StartObject)
                    return "состав READY: не найден объект d";

                var guilds = 0;
                while (reader.Read() && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                {
                    var key = (string)reader.Value;
                    if (key == "guilds")
                    {
                        reader.Read(); // [
                        while (reader.Read() && reader.TokenType == Newtonsoft.Json.JsonToken.StartObject)
                        {
                            guilds++;
                            while (reader.Read() && reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName)
                            {
                                var part = "guilds." + (string)reader.Value;
                                var start = info.LinePosition;
                                reader.Read();
                                reader.Skip();
                                Add(part, info.LinePosition - start);
                            }
                        }
                        continue;
                    }

                    var from = info.LinePosition;
                    reader.Read();
                    reader.Skip();
                    Add(key, info.LinePosition - from);
                }

                var total = data.Length;
                var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(sizes);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new System.Text.StringBuilder($"состав READY ({guilds} серверов): ");
                for (var i = 0; i < top.Count && i < 14; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{top[i].Key} {Size(top[i].Value)} ({top[i].Value * 100.0 / total:0}%)");
                }
                return sb.ToString();
            }
        }

        private void AnalyzeReadyLater(byte[] data)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(4000).ConfigureAwait(false); // after the start-up rush
                try
                {
                    var watch = Stopwatch.StartNew();
                    var line = AnalyzeReady(data);
                    this.AddDiagnostic(line + $"; разбор состава {watch.ElapsedMilliseconds} мс (только токенизация)");
                }
                catch (Exception ex)
                {
                    this.AddDiagnostic("состав READY: ошибка " + ex.GetType().Name);
                }
            });
        }

        /// <summary>Called after a gateway message was fully handled.</summary>
        private void ReportMessage(double totalMs)
        {
            var isReady = this._rxEvent == "READY";
            // READY always; other events only when large or slow
            if (!isReady && totalMs < 150 && this._rxBytes < 256 * 1024)
                return;

            var name = this._rxEvent ?? "?";
            var line = this._rxCompressedBytes > 0
                ? $"{name}: {Size(this._rxCompressedBytes)} сжато → {Size(this._rxBytes)}, распаковка {this._rxDecompressMs:0} мс, JSON {this._rxParseMs:0} мс, всего {totalMs:0} мс"
                : $"{name}: {Size(this._rxBytes)}, JSON {this._rxParseMs:0} мс, всего {totalMs:0} мс";
            this.AddDiagnostic(line);
        }
    }
}
