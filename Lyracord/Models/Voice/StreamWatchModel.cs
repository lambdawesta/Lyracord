using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unicord.Universal.Services;
using Unicord.Universal.Voice;

namespace Unicord.Universal.Models.Voice
{
    /// <summary>
    /// Watches someone's Go Live stream in the voice channel we are connected to:
    /// asks the gateway for the stream (op 20 STREAM_WATCH), waits for STREAM_CREATE / STREAM_SERVER_UPDATE,
    /// then lets the native <see cref="StreamViewer"/> receive and decrypt the H.264 frames.
    /// </summary>
    /// <summary>A received stream frame with its local arrival time (100 ns ticks since the watch started).</summary>
    internal sealed class StreamItem
    {
        public StreamItem(StreamFrame frame, long arrivalTicks)
        {
            Frame = frame;
            ArrivalTicks = arrivalTicks;
        }

        public StreamFrame Frame { get; }
        public long ArrivalTicks { get; }
    }

    internal sealed class StreamWatchModel : IDisposable
    {
        private const int OpStreamDelete = 19;
        private const int OpStreamWatch = 20;
        private const int MaxQueuedFrames = 45;

        private readonly DiscordChannel _channel;
        private readonly ulong _streamerId;
        private readonly string _streamKey;
        private TaskCompletionSource<ulong> _rtcServer = new TaskCompletionSource<ulong>();
        private TaskCompletionSource<(string endpoint, string token)> _server = new TaskCompletionSource<(string, string)>();
        private bool _accepted;
        private ulong _rtcChannelId;
        private readonly ConcurrentQueue<StreamItem> _frames = new ConcurrentQueue<StreamItem>();
        private readonly SemaphoreSlim _frameSignal = new SemaphoreSlim(0);
        private readonly ConcurrentQueue<StreamItem> _audio = new ConcurrentQueue<StreamItem>();
        private readonly SemaphoreSlim _audioSignal = new SemaphoreSlim(0);
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private const int MaxQueuedAudio = 50; // ~1 s of 20 ms Opus frames
        private StreamViewer _viewer;
        private bool _waitForKeyFrame = true;
        private int _disposed;
        private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public StreamWatchModel(DiscordChannel channel, ulong streamerId)
        {
            _channel = channel;
            _streamerId = streamerId;
            _streamKey = $"guild:{channel.GuildId}:{channel.Id}:{streamerId}";
        }

        public event EventHandler<string> StatusChanged;
        public event EventHandler<uint> Ended;

        private void Status(string text)
        {
            VoiceDiagnostics.Log("Трансляция: " + text);
            StatusChanged?.Invoke(this, text);
        }

        public async Task StartAsync()
        {
            var discord = DiscordManager.Discord;
            discord.UnknownEvent += OnUnknownEvent;

            // How a *viewer* identifies to the stream server is undocumented: try the variants in turn
            // (each with a fresh token) and remember the one the server accepts.
            // server choice: 0 = rtc_server_id (as the open-source sender), 1 = guild id, 2 = channel id
            var variants = new System.Collections.Generic.List<(int identify, int server)>
            {
                (2, 0), (0, 0), (2, 1), (0, 1), (2, 2), (1, 0), (3, 0),
            };
            var remembered = App.LocalSettings.Read("StreamConnectVariant", -1);
            if (remembered >= 0)
            {
                var pair = (remembered / 10, remembered % 10);
                variants.Remove(pair);
                variants.Insert(0, pair);
            }
            StreamLog($"==== watch {_streamKey}, gateway session {Prefix(discord.GatewaySessionId)}");

            uint lastCode = 0;
            for (var attempt = 0; attempt < variants.Count && !IsDisposed; attempt++)
            {
                var (variant, serverChoice) = variants[attempt];
                _rtcServer = new TaskCompletionSource<ulong>();
                _server = new TaskCompletionSource<(string, string)>();

                Status(attempt == 0 ? "запрос трансляции у Discord" : $"повторный запрос трансляции (вариант {variant})");
#pragma warning disable CS0618 // raw gateway payloads: STREAM_WATCH is not part of DSharpPlus
                await discord.SendPayloadAsync((GatewayOpCode)OpStreamWatch, new StreamKeyPayload { StreamKey = _streamKey });
#pragma warning restore CS0618

                var timeout = Task.Delay(TimeSpan.FromSeconds(15));
                if (await Task.WhenAny(Task.WhenAll(_rtcServer.Task, _server.Task), timeout) == timeout)
                    throw new TimeoutException("Discord не прислал сервер трансляции. Возможно, трансляция уже закончилась.");

                var rtcServerId = await _rtcServer.Task;
                var (endpoint, token) = await _server.Task;
                if (IsDisposed)
                    return;

                var serverId = serverChoice == 1 ? _channel.GuildId.GetValueOrDefault() : serverChoice == 2 ? _channel.Id : rtcServerId;
                Status($"сервер трансляции {endpoint?.Split(':')[0]}, вариант {variant}/{serverChoice}");
                StreamLog($"attempt {attempt}: identify variant {variant}, server_id {(serverChoice == 0 ? "rtc" : serverChoice == 1 ? "guild" : "channel")}={serverId}, token_len {token?.Length}");
                var options = new StreamViewerOptions
                {
                    Endpoint = endpoint,
                    Token = token,
                    SessionId = discord.GatewaySessionId,
                    ServerId = serverId,
                    CurrentUserId = discord.CurrentUser.Id,
                    StreamerUserId = _streamerId,
                    // the stream's DAVE group is STREAM_CREATE.rtc_channel_id (= rtc_server_id - 1 so far)
                    DaveGroupId = _rtcChannelId != 0 ? _rtcChannelId : rtcServerId - 1,
                    IdentifyVariant = variant,
                };

                var accepted = new TaskCompletionSource<uint>();
                var viewer = new StreamViewer(options);
                viewer.FrameReceived += OnFrameReceived;
                viewer.AudioReceived += OnAudioReceived;
                viewer.StatusChanged += (s, text) =>
                {
                    Status(text);
                    if (text == "ip discovery") // the server answered Identify with Ready
                        accepted.TrySetResult(0);
                };
                viewer.Disconnected += (s, code) =>
                {
                    if (!accepted.TrySetResult(code == 0 ? 1u : code) && _accepted)
                    {
                        Status($"сервер трансляции закрыл соединение: код {code}");
                        Ended?.Invoke(this, code);
                    }
                };
                _viewer = viewer;
                await viewer.ConnectAsync();

                var result = await Task.WhenAny(accepted.Task, Task.Delay(TimeSpan.FromSeconds(8)));
                if (result == accepted.Task && accepted.Task.Result == 0)
                {
                    _accepted = true;
                    App.LocalSettings.Save("StreamConnectVariant", variant * 10 + serverChoice);
                    Status($"сервер трансляции принял вариант {variant}/{serverChoice}");
                    StreamLog($"ACCEPTED identify variant {variant}, server choice {serverChoice}");
                    return;
                }

                lastCode = result == accepted.Task ? accepted.Task.Result : 0;
                Status(lastCode == 0 ? $"вариант {variant}/{serverChoice}: нет ответа" : $"вариант {variant}/{serverChoice}: отказ, код {lastCode}");
                StreamLog($"rejected: {(lastCode == 0 ? "no answer" : "code " + lastCode)}");
                _viewer = null;
                try { viewer.Dispose(); } catch (Exception ex) { Logger.LogError(ex); }
#pragma warning disable CS0618
                await discord.SendPayloadAsync((GatewayOpCode)OpStreamDelete, new StreamKeyPayload { StreamKey = _streamKey });
#pragma warning restore CS0618
                await Task.Delay(1500);
            }

            throw new Exception($"Сервер трансляции отклонил все варианты подключения (последний код {lastCode}). Пришлите журнал.");
        }

        private Task OnUnknownEvent(DiscordClient client, UnknownEventArgs e)
        {
            if (!e.EventName.StartsWith("STREAM_"))
                return Task.CompletedTask;

            try
            {
                var logged = JObject.Parse(e.Json);
                if (logged["token"] != null)
                    logged["token"] = "***";
                StreamLog($"< {e.EventName} {logged.ToString(Formatting.None)}");
            }
            catch (Exception)
            {
                StreamLog($"< {e.EventName} (unparsable)");
            }

            if (e.EventName != "STREAM_CREATE" && e.EventName != "STREAM_SERVER_UPDATE" && e.EventName != "STREAM_DELETE")
                return Task.CompletedTask;

            try
            {
                var data = JObject.Parse(e.Json);
                if ((string)data["stream_key"] != _streamKey)
                    return Task.CompletedTask;

                switch (e.EventName)
                {
                    case "STREAM_CREATE":
                        if (ulong.TryParse((string)data["rtc_channel_id"], out var rtcChannelId))
                            _rtcChannelId = rtcChannelId;
                        if (ulong.TryParse((string)data["rtc_server_id"], out var rtcServerId))
                            _rtcServer.TrySetResult(rtcServerId);
                        break;
                    case "STREAM_SERVER_UPDATE":
                        // only the host is ever logged, never the token
                        _server.TrySetResult(((string)data["endpoint"], (string)data["token"]));
                        break;
                    case "STREAM_DELETE":
                        Status("трансляция закончилась");
                        Ended?.Invoke(this, 0);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            return Task.CompletedTask;
        }

        private void OnFrameReceived(StreamViewer sender, StreamFrame frame)
        {
            if (IsDisposed)
                return;
            if (_waitForKeyFrame)
            {
                if (!frame.IsKeyFrame)
                    return;
                _waitForKeyFrame = false;
            }

            if (_frames.Count >= MaxQueuedFrames)
            {
                // the decoder fell behind: drop everything and restart from the next keyframe
                while (_frames.TryDequeue(out _)) { }
                _waitForKeyFrame = true;
                sender.RequestKeyFrame();
                if (!frame.IsKeyFrame)
                    return;
                _waitForKeyFrame = false;
            }

            _frames.Enqueue(new StreamItem(frame, _clock.Elapsed.Ticks));
            try { _frameSignal.Release(); } catch (ObjectDisposedException) { }
        }

        private void OnAudioReceived(StreamViewer sender, StreamFrame frame)
        {
            if (IsDisposed)
                return;
            while (_audio.Count >= MaxQueuedAudio && _audio.TryDequeue(out _)) { }
            _audio.Enqueue(new StreamItem(frame, _clock.Elapsed.Ticks));
            try { _audioSignal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Next video frame for the decoder, or null when stopped.</summary>
        public Task<StreamItem> NextFrameAsync(CancellationToken token) => NextAsync(_frames, _frameSignal, token);

        /// <summary>Next block of decoded stream audio (PCM s16le, 48 kHz, stereo), or null when stopped.</summary>
        public Task<StreamItem> NextAudioAsync(CancellationToken token) => NextAsync(_audio, _audioSignal, token);

        private async Task<StreamItem> NextAsync(ConcurrentQueue<StreamItem> queue, SemaphoreSlim signal, CancellationToken token)
        {
            while (!IsDisposed)
            {
                try
                {
                    await signal.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }

                if (queue.TryDequeue(out var item))
                    return item;
            }
            return null;
        }

        public void RequestKeyFrame() => _viewer?.RequestKeyFrame();

        public int QueuedFrames => _frames.Count;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _rtcServer.TrySetCanceled();
            _server.TrySetCanceled();

            var discord = DiscordManager.Discord;
            if (discord != null)
            {
                discord.UnknownEvent -= OnUnknownEvent;
#pragma warning disable CS0618
                _ = discord.SendPayloadAsync((GatewayOpCode)OpStreamDelete, new StreamKeyPayload { StreamKey = _streamKey });
#pragma warning restore CS0618
            }

            var viewer = Interlocked.Exchange(ref _viewer, null);
            try { viewer?.Dispose(); } catch (Exception ex) { Logger.LogError(ex); }
            try { _frameSignal.Release(); } catch (Exception) { }
            try { _audioSignal.Release(); } catch (Exception) { }
            try { _frameSignal.Dispose(); } catch (Exception) { }
            try { _audioSignal.Dispose(); } catch (Exception) { }
            FlushStreamLog();
        }

        private static string Prefix(string id) =>
            string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 4 ? id : id.Substring(0, 4)) + $"...({id.Length})";

        private const long StreamLogLimit = 256 * 1024;
        private static readonly object _logLock = new object();
        private static readonly object _logFileLock = new object();
        private static readonly System.Text.StringBuilder _logPending = new System.Text.StringBuilder();
        private static bool _logFlushScheduled;
        private static string _streamLogPath;

        /// <summary>LocalState\stream-events.log: gateway stream events (tokens redacted) and connect attempts, buffered off the UI thread and capped at 256 KB.</summary>
        internal static void StreamLog(string line)
        {
            lock (_logLock)
            {
                _logPending.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(line).Append("\r\n");
                if (_logFlushScheduled)
                    return;
                _logFlushScheduled = true;
            }
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);
                FlushStreamLog();
            });
        }

        internal static void FlushStreamLog()
        {
            string text;
            lock (_logLock)
            {
                _logFlushScheduled = false;
                if (_logPending.Length == 0)
                    return;
                text = _logPending.ToString();
                _logPending.Clear();
            }

            lock (_logFileLock)
            {
                try
                {
                    if (_streamLogPath == null)
                        _streamLogPath = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "stream-events.log");

                    var info = new System.IO.FileInfo(_streamLogPath);
                    if (info.Exists && info.Length > StreamLogLimit)
                    {
                        var old = System.IO.File.ReadAllText(_streamLogPath, System.Text.Encoding.UTF8);
                        var cut = old.IndexOf('\n', old.Length / 2);
                        System.IO.File.WriteAllText(_streamLogPath, cut >= 0 ? old.Substring(cut + 1) : "", System.Text.Encoding.UTF8);
                    }
                    System.IO.File.AppendAllText(_streamLogPath, text, System.Text.Encoding.UTF8);
                }
                catch (Exception)
                {
                }
            }
        }

        private sealed class StreamKeyPayload
        {
            [JsonProperty("stream_key")]
            public string StreamKey { get; set; }
        }
    }
}
