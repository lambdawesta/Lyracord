using System;
using System.ComponentModel;
using System.Threading;
using DSharpPlus.Entities;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models;
using Unicord.Universal.Models.Voice;
using Unicord.Universal.Services;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    public sealed class StreamPageArgs
    {
        public StreamPageArgs(DiscordChannel channel, ulong userId, string displayName)
        {
            Channel = channel;
            UserId = userId;
            DisplayName = displayName;
        }

        public DiscordChannel Channel { get; }
        public ulong UserId { get; }
        public string DisplayName { get; }
    }

    /// <summary>
    /// Plays someone's Go Live stream: H.264 frames and decoded audio from the native viewer go to Media Foundation
    /// through one MediaStreamSource. Each track is anchored to the arrival time of its first packet and then follows
    /// its own RTP clock (90 kHz video, 48 kHz audio); both share one forward re-base when the player gets ahead.
    /// </summary>
    public sealed partial class StreamPage : Page
    {
        private const string VolumeSetting = "StreamVolume";
        // small cushion so frames arriving with jitter are not already late
        private static readonly long LatencyTicks = TimeSpan.FromMilliseconds(120).Ticks;

        private StreamWatchModel _model;
        private MediaStreamSource _source;
        private MediaPlayer _player;
        private CancellationTokenSource _cts;
        private VideoStreamDescriptor _videoDescriptor;
        private AudioStreamDescriptor _audioDescriptor;

        private readonly TrackClock _videoClock = new TrackClock(90_000);
        private readonly TrackClock _audioClock = new TrackClock(48_000);
        private long _rebaseTicks;
        private readonly object _timelineLock = new object();

        private long _videoSamples, _audioSamples;
        private int _rebases;
        private DateTime _lastStats;
        private bool _firstFrameShown;
        private bool _loadingVolume;
        private VoiceConnectionModel _voice;

        private static VoiceConnectionModel CurrentVoice =>
            (Window.Current.Content.FindChild<DiscordPage>()?.DataContext as DiscordPageViewModel)?.VoiceModel;

        public StreamPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!(e.Parameter is StreamPageArgs args))
                return;

            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            TitleText.Text = Loc.F("Stream_Title", args.DisplayName);
            ToolTipService.SetToolTip(VoiceMuteToggle, Loc.T("Voice_Microphone"));
            ToolTipService.SetToolTip(VoiceDeafenToggle, Loc.T("Voice_Sound"));
            ToolTipService.SetToolTip(CloseButton, Loc.T("Stream_Close"));
            ToolTipService.SetToolTip(MuteButton, Loc.T("Stream_Volume"));
            StatusText.Text = Loc.T("Stream_Connecting");
            AttachVoice();

            // Every step goes to stream-events.log: on the phone the page used to stop before its first log line.
            var step = "начало";
            try
            {
                StreamWatchModel.StreamLog($"page: открываю трансляцию user={args.UserId.ToString().Substring(0, Math.Min(6, args.UserId.ToString().Length))}…");
                step = "модель";
                await StartAsync(args, s => { step = s; StreamWatchModel.StreamLog("page: " + s); });
                StreamWatchModel.StreamLog("page: запуск завершён");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                VoiceDiagnostics.Log($"Трансляция (шаг «{step}»)", ex);
                StreamWatchModel.StreamLog($"page: ОШИБКА на шаге «{step}» 0x{ex.HResult:X8} {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
                StatusText.Text = Loc.F("Stream_Error", step, $"0x{ex.HResult:X8} {ex.Message}");
                Loading.IsActive = false;
            }
        }

        private async System.Threading.Tasks.Task StartAsync(StreamPageArgs args, Action<string> step)
        {
            _cts = new CancellationTokenSource();
            _model = new StreamWatchModel(args.Channel, args.UserId);
            _model.StatusChanged += (s, text) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (text != null && text.StartsWith("unsupported_codec:", StringComparison.Ordinal))
                {
                    var codec = text.Substring("unsupported_codec:".Length);
                    StatusText.Text = Loc.F("Stream_UnsupportedCodec", codec);
                    Loading.IsActive = false;
                }
                else
                {
                    StatusText.Text = text;
                }
            });
            _model.Ended += (s, code) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StatusText.Text = code == 0 ? Loc.T("Stream_Ended") : Loc.F("Stream_Closed", code);
                Loading.IsActive = false;
            });

            // H.264 Annex B access units (Media Foundation reads the size from the SPS) + PCM from the native Opus decoder.
            step("описания потоков H264 + PCM");
            _videoDescriptor = new VideoStreamDescriptor(VideoEncodingProperties.CreateH264());
            _audioDescriptor = new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(48000, 2, 16));
            step("MediaStreamSource");
            _source = new MediaStreamSource(_videoDescriptor, _audioDescriptor);
            // set one by one: older builds (Windows 10 Mobile) may reject some of these
            TrySet("IsLive", () => _source.IsLive = true);
            TrySet("BufferTime", () => _source.BufferTime = TimeSpan.Zero);
            TrySet("CanSeek", () => _source.CanSeek = false);
            _source.SampleRequested += OnSampleRequested;

            step("MediaPlayer");
            _player = new MediaPlayer { AutoPlay = true };
            TrySet("RealTimePlayback", () => _player.RealTimePlayback = true);

            // On Windows 10 Mobile an active VoIP call (voice "call mode") silences ordinary media playback:
            // the stream's sound must play in the call's own (Communications) category to be heard.
            if (SystemPlatform.Mobile && App.LocalSettings.Read("PhoneCallMode", true))
            {
                TrySet("AudioCategory", () => _player.AudioCategory = MediaPlayerAudioCategory.Communications);
                StreamWatchModel.StreamLog($"page: звук трансляции в категории {_player.AudioCategory} (идёт VoIP-звонок)");
            }
            _player.MediaFailed += (s, a) => _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                StatusText.Text = Loc.F("Stream_DecoderError", $"{a.Error} 0x{a.ExtendedErrorCode?.HResult:X8}");
                VoiceDiagnostics.Log($"Трансляция: ошибка декодера {a.Error} 0x{a.ExtendedErrorCode?.HResult:X8} {a.ErrorMessage}");
                StreamWatchModel.StreamLog($"player: MediaFailed {a.Error} 0x{a.ExtendedErrorCode?.HResult:X8} {a.ErrorMessage}");
            });
            _player.PlaybackSession.PlaybackStateChanged += (session, o) => StreamWatchModel.StreamLog($"player: state {SafeState()}");
            step("источник проигрывателя");
            _player.Source = MediaSource.CreateFromMediaStreamSource(_source);
            step("привязка к экрану");
            Player.SetMediaPlayer(_player);

            step("громкость");
            _loadingVolume = true;
            VolumeSlider.Value = App.LocalSettings.Read(VolumeSetting, 100.0);
            _loadingVolume = false;
            ApplyVolume();

            step("подключение к трансляции");
            await _model.StartAsync();
        }

        private static void TrySet(string name, Action set)
        {
            try { set(); }
            catch (Exception ex) { StreamWatchModel.StreamLog($"page: {name} не поддерживается 0x{ex.HResult:X8} {ex.Message}"); }
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            MediaStreamSourceSampleRequestDeferral deferral = null;
            try
            {
                var request = args.Request;
                deferral = request.GetDeferral();
                var isAudio = request.StreamDescriptor is AudioStreamDescriptor;
                var model = _model;
                var cts = _cts;
                if (model == null || cts == null || cts.IsCancellationRequested)
                    return;

                var item = isAudio ? await model.NextAudioAsync(cts.Token) : await model.NextFrameAsync(cts.Token);
                if (item == null || cts.IsCancellationRequested)
                    return; // stopped: no sample ends the stream

                var time = PresentationTime(isAudio ? _audioClock : _videoClock, item);
                var sample = MediaStreamSample.CreateFromBuffer(item.Frame.Data, TimeSpan.FromTicks(time));
                if (isAudio)
                {
                    // PCM s16le stereo: 4 bytes per sample frame
                    sample.Duration = TimeSpan.FromTicks(item.Frame.Data.Length / 4 * 10_000_000L / 48_000L);
                    _audioSamples++;
                }
                else
                {
                    sample.KeyFrame = item.Frame.IsKeyFrame;
                    _videoSamples++;
                    if (!_firstFrameShown)
                    {
                        _firstFrameShown = true;
                        _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => Loading.IsActive = false);
                    }
                }
                request.Sample = sample;

                var now = DateTime.UtcNow;
                if (now - _lastStats > TimeSpan.FromSeconds(5))
                {
                    _lastStats = now;
                    StreamWatchModel.StreamLog($"player: video {_videoSamples}, audio {_audioSamples}, queued {model.QueuedFrames}, rebases {_rebases}, " +
                                               $"state {SafeState()}, position {SafePosition()?.TotalSeconds:F2}s");
                }
            }
            catch (Exception ex)
            {
                // the source may already be closed (E_ABORT) when the page is leaving
                StreamWatchModel.StreamLog($"player: sample request failed 0x{ex.HResult:X8} {ex.Message}");
            }
            finally
            {
                try { deferral?.Complete(); } catch (Exception) { }
            }
        }

        private long PresentationTime(TrackClock clock, StreamItem item)
        {
            lock (_timelineLock)
            {
                var time = clock.Next(item.Frame.RtpTimestamp, item.ArrivalTicks) + LatencyTicks + _rebaseTicks;

                // A live player drops samples whose time is already behind its clock; after any hiccup every following
                // sample would be late and playback would freeze. Move the timeline forward instead.
                var position = SafePosition();
                if (position.HasValue && time < position.Value.Ticks)
                {
                    var shift = position.Value.Ticks - time + TimeSpan.FromMilliseconds(30).Ticks;
                    _rebaseTicks += shift;
                    time += shift;
                    _rebases++;
                }
                return time;
            }
        }

        /// <summary>RTP timestamps of one track mapped to local time: anchored at the first packet's arrival.</summary>
        private sealed class TrackClock
        {
            private readonly long _rate;
            private bool _started;
            private uint _last;
            private long _anchorTicks;
            private long _elapsedTicks;

            public TrackClock(long rate) => _rate = rate;

            public long Next(uint rtpTimestamp, long arrivalTicks)
            {
                if (!_started)
                {
                    _started = true;
                    _last = rtpTimestamp;
                    _anchorTicks = arrivalTicks;
                }
                unchecked
                {
                    var delta = (int)(rtpTimestamp - _last);
                    _elapsedTicks += Math.Max(0, delta) * 10_000_000L / _rate;
                }
                _last = rtpTimestamp;
                return _anchorTicks + _elapsedTicks;
            }
        }

        private void AttachVoice()
        {
            DetachVoice();
            _voice = CurrentVoice;
            if (_voice != null)
                _voice.PropertyChanged += OnVoicePropertyChanged;
            SyncVoiceToggles();
        }

        private void DetachVoice()
        {
            if (_voice != null)
            {
                _voice.PropertyChanged -= OnVoicePropertyChanged;
                _voice = null;
            }
        }

        private void OnVoicePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VoiceConnectionModel.Muted) || e.PropertyName == nameof(VoiceConnectionModel.Deafened))
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    SyncVoiceToggles();
                    ApplyVolume();
                });
            }
        }

        private void SyncVoiceToggles()
        {
            var voice = _voice ?? CurrentVoice;
            var hasVoice = voice != null;
            VoiceMuteToggle.Visibility = hasVoice ? Visibility.Visible : Visibility.Collapsed;
            VoiceDeafenToggle.Visibility = hasVoice ? Visibility.Visible : Visibility.Collapsed;
            VoiceMuteToggle.IsChecked = voice?.Muted ?? false;
            VoiceDeafenToggle.IsChecked = voice?.Deafened ?? false;
        }

        private void VoiceMute_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var voice = _voice ?? CurrentVoice;
                if (voice != null)
                    voice.Muted = VoiceMuteToggle.IsChecked == true;
                SyncVoiceToggles();
                ApplyVolume();
                if (_landscape == true)
                    HideBarLater();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Трансляция: микрофон", ex);
            }
        }

        private void VoiceDeafen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var voice = _voice ?? CurrentVoice;
                if (voice != null)
                    voice.Deafened = VoiceDeafenToggle.IsChecked == true;
                SyncVoiceToggles();
                ApplyVolume();
                if (_landscape == true)
                    HideBarLater();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Трансляция: звук", ex);
            }
        }

        private void Volume_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loadingVolume)
                return;
            App.LocalSettings.Save(VolumeSetting, VolumeSlider.Value);
            ApplyVolume();
            if (_landscape == true)
                HideBarLater();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            if (_player == null)
                return;
            _player.IsMuted = !_player.IsMuted;
            ApplyVolume();
            if (_landscape == true)
                HideBarLater();
        }

        private void ApplyVolume()
        {
            if (_player == null)
                return;
            var deafened = (_voice ?? CurrentVoice)?.Deafened == true;
            var muted = deafened || _player.IsMuted || VolumeSlider.Value <= 0;
            _player.Volume = deafened ? 0.0 : VolumeSlider.Value / 100.0;
            MuteIcon.Glyph = muted ? "" : "";
        }

        private TimeSpan? SafePosition()
        {
            try { return _player?.PlaybackSession?.Position; } catch (Exception) { return null; }
        }

        private string SafeState()
        {
            try { return _player?.PlaybackSession?.PlaybackState.ToString() ?? "-"; } catch (Exception) { return "?"; }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
                Frame.GoBack();
            else
                Stop();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            DetachVoice();
            _barTimer?.Stop();
            LeaveFullScreen();
            Stop();
        }

        // ------------------------------------------------------------------ landscape: full screen video

        private bool? _landscape;
        private bool _enteredFullScreen;
        private DispatcherTimer _barTimer;

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var landscape = e.NewSize.Width > e.NewSize.Height;
            if (_landscape != landscape)
                ApplyOrientation(landscape);
        }

        /// <summary>
        /// Landscape: the video takes the whole screen (system bars hidden) and the bar lies over it, hiding after a
        /// few seconds. Portrait: bar above the video. Turning the phone used to leave the video laid out for the old
        /// size, pushed down below the screen.
        /// </summary>
        private void ApplyOrientation(bool landscape)
        {
            _landscape = landscape;
            try
            {
                Grid.SetRow(Player, landscape ? 0 : 1);
                Grid.SetRowSpan(Player, landscape ? 2 : 1);
                Grid.SetRow(Loading, landscape ? 0 : 1);
                Grid.SetRowSpan(Loading, landscape ? 2 : 1);
                Bar.Visibility = Visibility.Visible;

                if (landscape)
                {
                    try
                    {
                        var view = Windows.UI.ViewManagement.ApplicationView.GetForCurrentView();
                        var entered = view.IsFullScreenMode || view.TryEnterFullScreenMode();
                        if (entered && !_enteredFullScreen)
                            _enteredFullScreen = true;
                        StreamWatchModel.StreamLog($"page: горизонталь, полный экран {(entered ? "включён" : "не включился")}");
                    }
                    catch (Exception ex) { StreamWatchModel.StreamLog("page: полный экран " + ex.Message); }
                    SetStatusBar(false);
                    HideBarLater();
                }
                else
                {
                    _barTimer?.Stop();
                    LeaveFullScreen();
                }

                // make the video surface follow the new size right away
                Player.Stretch = Stretch.None;
                Player.Stretch = Stretch.Uniform;
                Player.InvalidateMeasure();
                Player.InvalidateArrange();
            }
            catch (Exception ex)
            {
                StreamWatchModel.StreamLog("page: поворот экрана " + ex.Message);
            }
        }

        private void Player_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_landscape != true)
                return;
            if (Bar.Visibility == Visibility.Visible)
            {
                _barTimer?.Stop();
                Bar.Visibility = Visibility.Collapsed;
            }
            else
            {
                Bar.Visibility = Visibility.Visible;
                HideBarLater();
            }
        }

        private void HideBarLater()
        {
            if (_barTimer == null)
            {
                _barTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _barTimer.Tick += (s, a) =>
                {
                    _barTimer.Stop();
                    if (_landscape == true)
                        Bar.Visibility = Visibility.Collapsed;
                };
            }
            _barTimer.Stop();
            _barTimer.Start();
        }

        /// <summary>The phone's status bar (clock, battery): hidden while the video fills a landscape screen.</summary>
        private static async void SetStatusBar(bool visible)
        {
            try
            {
                if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
                    return;
                var bar = Windows.UI.ViewManagement.StatusBar.GetForCurrentView();
                if (visible)
                    await bar.ShowAsync();
                else
                    await bar.HideAsync();
            }
            catch (Exception) { }
        }

        private void LeaveFullScreen()
        {
            SetStatusBar(true);
            if (!_enteredFullScreen)
                return;
            _enteredFullScreen = false;
            try { Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().ExitFullScreenMode(); } catch (Exception) { }
        }

        // hardware/system back returns to the voice channel instead of closing the app
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled)
                return;
            e.Handled = true;
            if (Frame?.CanGoBack == true)
                Frame.GoBack();
            else
                Stop();
        }

        private void Stop()
        {
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch (Exception) { }

            var source = _source;
            _source = null;
            if (source != null)
                source.SampleRequested -= OnSampleRequested;

            var player = _player;
            _player = null;
            try
            {
                Player.SetMediaPlayer(null);
                if (player != null)
                {
                    player.Pause();
                    player.Source = null;
                    player.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            // Dispose the native StreamViewer off the UI thread so closing sockets/opus never stalls navigation.
            var model = _model;
            _model = null;
            if (model != null || cts != null)
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try { model?.Dispose(); } catch (Exception ex) { Logger.LogError(ex); }
                    try { cts?.Dispose(); } catch (Exception) { }
                });
            }
        }
    }
}
