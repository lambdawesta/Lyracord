using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net.Abstractions;
using Unicord.Universal.Services;
using Unicord.Universal.Voice;
using Unicord.Universal.Voice.Background;
using Unicord.Universal.Voice.Transport;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Calls;
using Windows.ApplicationModel.Resources;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Unicord.Universal.Models.Voice
{
    public class VoiceConnectionModel : ViewModelBase
    {
        private AppServiceConnection _appServiceConnection;
        private VoipCallCoordinator _voipCallCoordinator;
        private ResourceLoader _strings;
        private VoiceState _state = VoiceState.None;
        private TaskCompletionSource<VoiceStateUpdateEventArgs> _voiceStateUpdateCompletion;
        private TaskCompletionSource<VoiceServerUpdateEventArgs> _voiceServerUpdateCompletion;
        private bool _appServiceConnected;
        private string _pcSessionId;
        private string _pcEndpoint;
        private string _pcToken;
        private int _pcRejoins;
        private int _pcStarts;
        private DateTime _pcHealthySinceUtc;
        private bool _pcStabilityResetDone;
        private volatile bool _pcClosing;
        private readonly SemaphoreSlim _pcLock = new SemaphoreSlim(1, 1);
        private uint _webSocketPing;
        private uint _udpPing;
        private string _connectionStatus;

        // Windows 10 Mobile refuses the VoIP call reservation and the background voice service
        // (ERROR_NOT_SUPPORTED), so on phones the native VoiceClient runs inside the app instead.
        // Voice then lives while the app is in the foreground; the screen is kept on meanwhile.
        private readonly bool _inApp = SystemPlatform.Mobile;
        private VoiceClient _inAppClient;
        private Windows.System.Display.DisplayRequest _displayRequest;
        // A foreground VoIP call puts the phone's audio into call mode: without it the
        // Communications streams are silent or heavily attenuated on Windows 10 Mobile.
        private VoipPhoneCall _inAppCall;
        // The voice session can be (re)assigned by Discord after the first VOICE_SERVER_UPDATE:
        // the token in the first update is then rejected with 4006. Keep the latest values and
        // restart the native client whenever they change.
        private readonly System.Threading.SemaphoreSlim _inAppLock = new System.Threading.SemaphoreSlim(1, 1);
        private string _inAppSessionId;
        private string _inAppEndpoint;
        private string _inAppToken;
        private int _inAppRejoins;
        private int _inAppStarts;
        private DateTime _inAppHealthySinceUtc;
        private bool _inAppStabilityResetDone;
        private volatile bool _inAppClosing;
        private bool _joinSoundPlayed;
        private int _inAppCallCounted;
        private static int _activeInAppCalls;
        internal static bool HasActiveInAppCall => Volatile.Read(ref _activeInAppCalls) > 0;

        private bool _audioRoutingSubscribed;
        private int _lastAudioEndpoint = -1;

        public event EventHandler AudioRoutingChanged;

        public static bool IsAudioRoutingSupported =>
            Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Phone.Media.Devices.AudioRoutingManager");

        // Prevents concurrent DisconnectAsync() calls (double-tap, system VoIP EndRequested + button, etc.).
        private readonly object _disconnectGate = new object();
        private Task _disconnectTask;

        // Keeps the app running (and voice going) while minimised or behind the lock screen on Windows 10 Mobile:
        // 1) ExtendedExecutionForegroundSession(Unconstrained) with rescap:extendedExecutionUnconstrained;
        // 2) fallback to ExtendedExecutionSession(Unspecified);
        // 3) silent looping Communications MediaPlayer so uap3:backgroundMediaPlayback also keeps PLM awake.
        private readonly object _keepAliveLock = new object();
        private Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundSession _foregroundSession;
        private Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession _backgroundSession;
        private MediaPlayer _keepAlivePlayer;
        private InMemoryRandomAccessStream _keepAliveStream;

        public string ConnectionStatus { get => _connectionStatus; set => OnPropertySet(ref _connectionStatus, value); }
        public DiscordChannel Channel { get; }

        // The setters take the value they are given (they used to flip the state). The sidebar toggles are bound
        // two-way: with flipping setters any change made elsewhere (the call view's toolbar) bounced back and
        // forth between binding and model forever, and the app died.
        public bool Muted
        {
            get => _state != VoiceState.None;
            set
            {
                if (value == Muted)
                    return;
                // unmuting while deafened undeafens too, like Discord
                OnPropertySet(ref _state, value ? (_state | VoiceState.Muted) : VoiceState.None);
                InvokePropertyChanged(nameof(Deafened));
            }
        }

        public bool Deafened
        {
            get => _state.HasFlag(VoiceState.Deafened);
            set
            {
                if (value == Deafened)
                    return;
                if (value)
                    _mutedBeforeDeafen = _state.HasFlag(VoiceState.Muted);
                OnPropertySet(ref _state, value ? (VoiceState.Muted | VoiceState.Deafened) : (_mutedBeforeDeafen ? VoiceState.Muted : VoiceState.None));
                InvokePropertyChanged(nameof(Muted));
            }
        }

        private bool _mutedBeforeDeafen;

        public uint WebSocketPing { get => _webSocketPing; set => OnPropertySet(ref _webSocketPing, value); }
        public uint UdpPing { get => _udpPing; set => OnPropertySet(ref _udpPing, value); }
        public event EventHandler<EventArgs> Disconnected;

        private string ChannelDisplayName => VoiceCallViewModel.GetChannelTitle(Channel);

        /// <summary>
        /// If voice is connected before Unicord launched, returns a VoiceConnectionModel for 
        /// that connection, otherwise null.
        /// </summary>
        public static async Task<VoiceConnectionModel> FindExistingConnectionAsync()
        {
            // In-app voice on phones never outlives the app, so there is nothing to find.
            if (SystemPlatform.Mobile)
                return null;

            var connection = new AppServiceConnection()
            {
                AppServiceName = "com.wankerr.Unicord.Voice",
                PackageFamilyName = Package.Current.Id.FamilyName
            };

            if (await connection.OpenAsync() != AppServiceConnectionStatus.Success)
            {
                connection.Dispose();
                return null;
            }

            var strings = ResourceLoader.GetForViewIndependentUse("Voice");
            var stateRequest = new ValueSet() { ["req"] = (uint)VoiceServiceRequest.StateRequest };
            var info = await connection.SendMessageAsync(stateRequest);
            if (info == null || info.Message == null)
                return null;

            var state = (VoiceServiceState)(uint)info.Message["state"];
            if (state == VoiceServiceState.ReadyToConnect)
            {
                connection.Dispose();
                return null;
            }

            var channel_id = (ulong)info.Message["channel_id"];
            var guild_id = (ulong)info.Message["guild_id"];
            var muted = (bool)info.Message["muted"];
            var deafened = (bool)info.Message["deafened"];

            var channel = DiscordManager.Discord.Guilds[guild_id].Channels[channel_id];
            var vstate = VoiceState.None;
            if (muted)
                vstate |= VoiceState.Muted;
            if (deafened)
                vstate = VoiceState.Muted | VoiceState.Deafened;

            return new VoiceConnectionModel(channel, vstate, connection) { ConnectionStatus = string.Format(strings.GetString("ConnectedStateFormat"), channel.Name), _strings = strings };
        }

        public VoiceConnectionModel(DiscordChannel channel)
        {
            _voiceStateUpdateCompletion = new TaskCompletionSource<VoiceStateUpdateEventArgs>();
            _voiceServerUpdateCompletion = new TaskCompletionSource<VoiceServerUpdateEventArgs>();
            _strings = ResourceLoader.GetForViewIndependentUse("Voice");

            Channel = channel;
            ConnectionStatus = _strings.GetString("InitialConnectionState");
            PropertyChanged += OnPropertyChanged;

            if (_inApp)
                return;

            _voipCallCoordinator = VoipCallCoordinator.GetDefault();
            _appServiceConnection = new AppServiceConnection()
            {
                AppServiceName = "com.wankerr.Unicord.Voice",
                PackageFamilyName = Package.Current.Id.FamilyName
            };

            _appServiceConnection.RequestReceived += OnRequestReceived;
            _appServiceConnection.ServiceClosed += OnServiceClosed;
        }

        private VoiceConnectionModel(DiscordChannel channel, VoiceState vstate, AppServiceConnection connection)
        {
            Channel = channel;
            PropertyChanged += OnPropertyChanged;

            _voipCallCoordinator = VoipCallCoordinator.GetDefault();
            _appServiceConnection = connection;
            _appServiceConnection.RequestReceived += OnRequestReceived;
            _appServiceConnection.ServiceClosed += OnServiceClosed;
            _state = vstate;
        }

        private async void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            try
            {
                // both setters raise Muted and Deafened; apply the whole state once
                if (e.PropertyName == nameof(Muted))
                    await ApplyMuteStateAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                VoiceDiagnostics.Log("Микрофон/звук", ex);
            }
        }

        public async Task UpdatePreferredAudioDevicesAsync(string audioRender, string audioCapture)
        {
            if (_inApp)
            {
                _inAppClient?.UpdateAudioDevices();
                return;
            }

            var set = new ValueSet() { ["req"] = (uint)VoiceServiceRequest.SettingsUpdate };
            if (audioRender != null)
                set["output_device"] = audioRender;

            if (audioCapture != null)
                set["input_device"] = audioCapture;

            await SendRequestAsync(set);
        }

        public async Task ConnectAsync()
        {

            if (_inApp)
            {
                await ConnectInAppAsync();
                return;
            }

            ConnectionStatus = _strings.GetString("ConnectionState1");

            var appServiceStatus = await _appServiceConnection.OpenAsync();
            if (appServiceStatus != AppServiceConnectionStatus.Success)
                throw new Exception("Unable to connect to AppService! " + appServiceStatus);

            var stateRequest = new ValueSet() { ["req"] = (uint)VoiceServiceRequest.StateRequest };
            var response = await SendRequestAsync(stateRequest);
            var state = (VoiceServiceState)(uint)response["state"];

            if (state == VoiceServiceState.Connected)
            {
                var channel_id = (ulong)response["channel_id"];
                var guild_id = (ulong)response["guild_id"];

                if (channel_id != Channel.Id && guild_id != Channel.GuildId)
                {
                    ConnectionStatus = _strings.GetString("ConnectionState2");

                    var disconnectRequest = new ValueSet() { ["req"] = (uint)VoiceServiceRequest.DisconnectRequest };
                    await SendRequestAsync(disconnectRequest);
                }
            }

            _appServiceConnected = true;
            var status = await ReserveCallResourcesAsync();
            if (status != VoipPhoneCallResourceReservationStatus.Success)
                throw new Exception("Unable to reserve call resources!");

            ConnectionStatus = _strings.GetString("ConnectionState3");

            discord.VoiceStateUpdated += OnOtherVoiceStateUpdated;
            discord.VoiceStateUpdated += OnVoiceStateUpdated;
            discord.VoiceServerUpdated += OnVoiceServerUpdated;
            await SendVoiceStateUpdateAsync(_state, Channel.Id);
            _ = RingDmCallIfNeededAsync();

            var vstu = await _voiceStateUpdateCompletion.Task.ConfigureAwait(false);
            var vsru = await _voiceServerUpdateCompletion.Task.ConfigureAwait(false);
            _pcSessionId = vstu.SessionId;
            _pcEndpoint = vsru.Endpoint;
            _pcToken = vsru.VoiceToken;

            ConnectionStatus = string.Format(_strings.GetString("ConnectionState4Format"), ChannelDisplayName);
            await SendPcConnectRequestAsync("первый запуск").ConfigureAwait(false);
        }

        private async Task SendPcConnectRequestAsync(string reason)
        {
            var limitExceeded = false;
            await _pcLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_pcClosing || !_appServiceConnected)
                    return;

                if (++_pcStarts > 5)
                {
                    VoiceDiagnostics.Log($"ПК-голос ({reason}): слишком много перезапусков подряд (5) — завершаю звонок");
                    limitExceeded = true;
                    return;
                }

                if (string.IsNullOrEmpty(_pcToken) || string.IsNullOrEmpty(_pcEndpoint) || string.IsNullOrEmpty(_pcSessionId))
                    return;

                _pcHealthySinceUtc = DateTime.MinValue;
                _pcStabilityResetDone = false;

                var inputDeviceId = App.LocalSettings.Read<string>("InputDevice", null);
                var outputDeviceId = App.LocalSettings.Read<string>("OutputDevice", null);

                var connectionRequest = new ValueSet()
                {
                    ["req"] = (uint)VoiceServiceRequest.GuildConnectRequest,
                    ["channel_id"] = Channel.Id,
                    ["guild_id"] = Channel.Guild?.Id ?? Channel.Id,
                    ["user_id"] = discord.CurrentUser.Id,
                    ["endpoint"] = _pcEndpoint,
                    ["token"] = _pcToken,
                    ["session_id"] = _pcSessionId,
                    ["contact_name"] = Channel.Guild != null ? $"{ChannelDisplayName} - {Channel.Guild.Name}" : ChannelDisplayName,
                    ["input_device"] = inputDeviceId,
                    ["output_device"] = outputDeviceId,
                    ["muted"] = Muted,
                    ["deafened"] = Deafened,
                    ["noise_suppression"] = App.LocalSettings.Read("NoiseSuppression", (uint)NoiseSuppressionLevel.Medium),
                    ["auto_gain_control"] = App.LocalSettings.Read("AutomaticGainControl", true),
                    ["echo_cancellation"] = App.LocalSettings.Read("EchoCancellation", true),
                    ["voice_activity"] = App.LocalSettings.Read("VoiceActivity", true),
                };

                await SendRequestAsync(connectionRequest).ConfigureAwait(false);
            }
            finally
            {
                _pcLock.Release();
            }

            if (limitExceeded)
                _ = DisconnectAsync();
        }

        private async Task ConnectInAppAsync()
        {
            VoiceDiagnostics.Clear();
            VoiceDiagnostics.Log($"Подключение к каналу (тип {Channel.Type}), режим звонка={App.LocalSettings.Read("PhoneCallMode", true)}");

            // The native module opens the microphone from a worker thread, where Windows cannot
            // show the consent prompt; ask for access here, on the UI thread, first.
            try
            {
                using (var capture = new Windows.Media.Capture.MediaCapture())
                {
                    await capture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings
                    {
                        StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio
                    });
                }
                VoiceDiagnostics.Log("Доступ к микрофону: есть");
            }
            catch (UnauthorizedAccessException)
            {
                VoiceDiagnostics.Log("Доступ к микрофону: ЗАПРЕЩЁН");
                throw new Exception("Нет доступа к микрофону. Разрешите его: Параметры → Конфиденциальность → Микрофон.");
            }
            catch (Exception ex)
            {
                // No microphone or another app holds it: listening may still work.
                VoiceDiagnostics.Log("Проверка микрофона", ex);
                Logger.LogError(ex);
            }

            if (Interlocked.Exchange(ref _inAppCallCounted, 1) == 0)
                Interlocked.Increment(ref _activeInAppCalls);

            try
            {
                await RequestBackgroundExecutionAsync();

                ConnectionStatus = _strings.GetString("ConnectionState3");

                discord.VoiceStateUpdated += OnVoiceStateUpdated;
                discord.VoiceServerUpdated += OnVoiceServerUpdated;
                discord.VoiceStateUpdated += OnOtherVoiceStateUpdated;
                await SendVoiceStateUpdateAsync(_state, Channel.Id);
                _ = RingDmCallIfNeededAsync();

                await _voiceStateUpdateCompletion.Task.ConfigureAwait(false);
                await _voiceServerUpdateCompletion.Task.ConfigureAwait(false);
                // Discord may re-assign the voice server right after the first update; give it a moment
                // so we start with the final token (later updates still restart the client).
                await Task.Delay(500).ConfigureAwait(false);

                if (_inAppClosing)
                    return;

                ConnectionStatus = string.Format(_strings.GetString("ConnectionState4Format"), ChannelDisplayName);

                // Enter call mode and pick the loudspeaker BEFORE the native client opens its audio
                // streams: switching the phone into a call re-routes audio and silences streams that
                // were opened earlier, which the native client does not recover from on its own.
                if (App.LocalSettings.Read("PhoneCallMode", true))
                {
                    StartInAppCall();
                    ActivateInAppCall();
                    if (_inAppCall != null)
                        await Task.Delay(400).ConfigureAwait(false);
                }

                await StartNativeClientAsync("первый запуск").ConfigureAwait(false);
                if (_inAppClosing)
                    return;
                StartBackgroundAudioKeepAlive();
            }
            catch (OperationCanceledException) when (_inAppClosing)
            {
                return;
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Подключение звука", ex);
                await CloseInAppClientAsync().ConfigureAwait(false);
                await SendVoiceStateUpdateAsync(VoiceState.None, null);
                throw;
            }

            syncContext.Post(_ =>
            {
                try
                {
                    _displayRequest = new Windows.System.Display.DisplayRequest();
                    _displayRequest.RequestActive();
                }
                catch (Exception)
                {
                    _displayRequest = null;
                }
            }, null);

            ConnectionStatus = string.Format(_strings.GetString("ConnectedStateFormat"), ChannelDisplayName);
        }

        private VoiceClientOptions CreateInAppOptions()
        {
            return new VoiceClientOptions
            {
                Token = _inAppToken,
                SessionId = _inAppSessionId,
                Endpoint = _inAppEndpoint,
                GuildId = Channel.Guild?.Id ?? Channel.Id,
                ChannelId = Channel.Id,
                CurrentUserId = discord.CurrentUser.Id,
                // Empty means the default device, which on a phone is what we want.
                PreferredRecordingDevice = App.LocalSettings.Read<string>("InputDevice", null) ?? string.Empty,
                PreferredPlaybackDevice = App.LocalSettings.Read<string>("OutputDevice", null) ?? string.Empty,
                SuppressionLevel = (NoiseSuppressionLevel)App.LocalSettings.Read("NoiseSuppression", (uint)NoiseSuppressionLevel.Medium),
                EchoCancellation = App.LocalSettings.Read("EchoCancellation", true),
                VoiceActivity = App.LocalSettings.Read("VoiceActivity", true),
                AutomaticGainControl = App.LocalSettings.Read("AutomaticGainControl", true),
            };
        }

        /// <summary>
        /// (Re)starts the native voice client with the latest session id, endpoint and token.
        /// The VoIP call (call mode, loudspeaker) is kept as it is.
        /// </summary>
        private async Task StartNativeClientAsync(string reason)
        {
            var limitExceeded = false;
            await _inAppLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_inAppClosing)
                    return;

                if (++_inAppStarts > 5)
                {
                    VoiceDiagnostics.Log($"Запуск голоса ({reason}) пропущен: слишком много перезапусков подряд (5) — завершаю звонок");
                    limitExceeded = true;
                    return;
                }

                var old = _inAppClient;
                _inAppClient = null;
                if (old != null)
                {
                    try { old.Dispose(); } catch (Exception ex) { Logger.LogError(ex); }
                }

                if (string.IsNullOrEmpty(_inAppToken) || string.IsNullOrEmpty(_inAppEndpoint) || string.IsNullOrEmpty(_inAppSessionId))
                {
                    VoiceDiagnostics.Log($"Запуск голоса ({reason}) отложен: нет сервера/токена/сессии");
                    return;
                }

                VoiceDiagnostics.Log($"Запуск голоса ({reason}): сервер {_inAppEndpoint.Split(':')[0]}; {VoiceDiagnostics.CurrentRouting()}");
                var client = new VoiceClient(CreateInAppOptions());
                VoiceDiagnostics.Log($"VoiceClient создан (Opus {VoiceClient.OpusVersion}, libsodium {VoiceClient.SodiumVersion})");
                var firstWs = true;
                var firstUdp = true;
                _inAppHealthySinceUtc = DateTime.MinValue;
                _inAppStabilityResetDone = false;
                client.WebSocketPingUpdated += (s, ping) =>
                {
                    if (firstWs) VoiceDiagnostics.Log($"Первый пинг веб-сокета: {ping} мс");
                    firstWs = false;
                    WebSocketPing = ping;
                };
                client.UdpSocketPingUpdated += (s, ping) =>
                {
                    if (firstUdp)
                    {
                        VoiceDiagnostics.Log($"Первый пинг UDP (голосовой канал связи работает): {ping} мс");
                        firstUdp = false;
                        _inAppHealthySinceUtc = DateTime.UtcNow;
                    }
                    else if (!_inAppStabilityResetDone && _inAppHealthySinceUtc != DateTime.MinValue &&
                             (DateTime.UtcNow - _inAppHealthySinceUtc).TotalSeconds >= 30)
                    {
                        _inAppStabilityResetDone = true;
                        if (_inAppStarts > 1 || _inAppRejoins > 0)
                        {
                            _inAppStarts = 0;
                            _inAppRejoins = 0;
                            VoiceDiagnostics.Log("Голосовое соединение стабильно (30 с) — счётчик переподключений сброшен");
                        }
                    }
                    UdpPing = ping;
                };
                client.Disconnected += (s, code) => OnNativeClientDisconnected(client, code);
                client.SpeakingChanged += (s, value) =>
                {
                    var uid = VoiceSpeakingState.SetFromNative(value);
                    if (uid != 0 && Channel?.Guild == null && uid != discord.CurrentUser?.Id)
                        _ = discord.EnsurePrivateVoiceParticipantAsync(Channel.Id, uid);
                };
                client.Muted = Muted;
                client.Deafened = Deafened;
                _inAppClient = client;

                await client.ConnectAsync();
                VoiceDiagnostics.Log("VoiceClient.ConnectAsync: успешно");
                if (!_joinSoundPlayed)
                {
                    _joinSoundPlayed = true;
                    VoiceSounds.Play(VoiceSounds.Kind.SelfJoined);
                }

                if (_inAppCall != null)
                {
                    // Re-open the devices once more under the final call routing.
                    try
                    {
                        client.UpdateAudioDevices();
                        VoiceDiagnostics.Log("UpdateAudioDevices: выполнено");
                    }
                    catch (Exception ex)
                    {
                        VoiceDiagnostics.Log("UpdateAudioDevices", ex);
                        Logger.LogError(ex);
                    }
                }
                VoiceDiagnostics.Log($"После подключения: {VoiceDiagnostics.CurrentRouting()}");
            }
            finally
            {
                _inAppLock.Release();
            }

            if (limitExceeded)
                _ = DisconnectAsync();
        }

        private void OnNativeClientDisconnected(VoiceClient sender, uint code)
        {
            VoiceDiagnostics.Log($"Голосовой сервер закрыл соединение: код {code}");
            if (_inAppClosing || sender != _inAppClient)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var final = code == 4004 || code == 4014 || code == 4017 || code == 4021 || code == 4022;
                    if (!final && _inAppRejoins < 3)
                    {
                        // Session no longer valid / timed out: ask Discord for a fresh voice session.
                        // The resulting VOICE_STATE_UPDATE / VOICE_SERVER_UPDATE restart the client.
                        _inAppRejoins++;
                        VoiceDiagnostics.Log($"Запрос новой голосовой сессии у Discord (попытка {_inAppRejoins} из 3)");
                        // The next VOICE_SERVER_UPDATE (if any) restarts the client; no leave/rejoin.
                        await SendVoiceStateUpdateAsync(_state, Channel.Id);
                        return;
                    }

                    if (code == 4017)
                        VoiceDiagnostics.Log("Канал требует DAVE, а модуль его не согласовал");

                    await DisconnectAsync();
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Обработка отключения", ex);
                    Logger.LogError(ex);
                }
            });
        }

        private void StartInAppCall()
        {
            try
            {
                var coordinator = VoipCallCoordinator.GetDefault();
                var contactName = Channel.Guild != null ? $"{ChannelDisplayName} - {Channel.Guild.Name}" : ChannelDisplayName;
                _inAppCall = coordinator.RequestNewOutgoingCall(
                    string.Empty, contactName, "Lyracord", VoipPhoneCallMedia.Audio);
                if (_inAppCall != null)
                    _inAppCall.EndRequested += OnInAppCallEndRequested;
                VoiceDiagnostics.Log(_inAppCall != null ? "VoIP-звонок создан" : "VoIP-звонок НЕ создан (null)");
            }
            catch (Exception ex)
            {
                // Voice still connects without call mode, just with the limitations it had before.
                VoiceDiagnostics.Log("VoIP-звонок", ex);
                Logger.LogError(ex);
                _inAppCall = null;
            }
        }

        private void ActivateInAppCall()
        {
            if (_inAppCall == null)
                return;

            try
            {
                _inAppCall.NotifyCallActive();
                VoiceDiagnostics.Log("VoIP-звонок активен");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("NotifyCallActive", ex);
                Logger.LogError(ex);
                return;
            }

            SubscribeAudioRouting();
            RouteToLoudspeakerIfNoHeadset();
            VoiceDiagnostics.Log($"После выбора громкой связи: {VoiceDiagnostics.CurrentRouting()}");
            AudioRoutingChanged?.Invoke(this, EventArgs.Empty);
        }

        public CallAudioRoute CurrentAudioRoute
        {
            get
            {
                if (!IsAudioRoutingSupported)
                    return CallAudioRoute.Speakerphone;
                try
                {
                    var current = Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault().GetAudioEndpoint();
                    switch (current)
                    {
                        case Windows.Phone.Media.Devices.AudioRoutingEndpoint.Speakerphone:
                            return CallAudioRoute.Speakerphone;
                        case Windows.Phone.Media.Devices.AudioRoutingEndpoint.Bluetooth:
                        case Windows.Phone.Media.Devices.AudioRoutingEndpoint.BluetoothWithNoiseAndEchoCancellation:
                            return CallAudioRoute.Bluetooth;
                        case Windows.Phone.Media.Devices.AudioRoutingEndpoint.WiredHeadset:
                        case Windows.Phone.Media.Devices.AudioRoutingEndpoint.WiredHeadsetSpeakerOnly:
                            return CallAudioRoute.Headset;
                        default:
                            return CallAudioRoute.Earpiece;
                    }
                }
                catch (Exception)
                {
                    return CallAudioRoute.Speakerphone;
                }
            }
        }

        /// <summary>
        /// Cycles the phone's call audio output: Speakerphone <-> Earpiece (or Wired Headset),
        /// including Bluetooth when a Bluetooth headset is connected.
        /// </summary>
        public void CycleAudioRoute()
        {
            if (!IsAudioRoutingSupported)
                return;

            try
            {
                var routing = Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault();
                var current = routing.GetAudioEndpoint();
                var available = routing.AvailableAudioEndpoints;
                var hasBt = available.HasFlag(Windows.Phone.Media.Devices.AvailableAudioRoutingEndpoints.Bluetooth);

                Windows.Phone.Media.Devices.AudioRoutingEndpoint target;
                if (current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.Speakerphone)
                {
                    target = hasBt
                        ? Windows.Phone.Media.Devices.AudioRoutingEndpoint.Bluetooth
                        : Windows.Phone.Media.Devices.AudioRoutingEndpoint.Earpiece;
                }
                else if (current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.Bluetooth ||
                         current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.BluetoothWithNoiseAndEchoCancellation)
                {
                    target = Windows.Phone.Media.Devices.AudioRoutingEndpoint.Earpiece;
                }
                else
                {
                    target = Windows.Phone.Media.Devices.AudioRoutingEndpoint.Speakerphone;
                }

                routing.SetAudioEndpoint(target);
                VoiceDiagnostics.Log($"Переключение звука вручную → {target}; теперь {VoiceDiagnostics.CurrentRouting()}");
                AudioRoutingChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Переключение маршрута звука", ex);
                Logger.LogError(ex);
            }
        }

        private void SubscribeAudioRouting()
        {
            if (!IsAudioRoutingSupported || _audioRoutingSubscribed)
                return;
            try
            {
                var routing = Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault();
                _lastAudioEndpoint = (int)routing.GetAudioEndpoint();
                routing.AudioEndpointChanged += OnAudioEndpointChanged;
                _audioRoutingSubscribed = true;
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Подписка на маршрут звука", ex);
            }
        }

        private void UnsubscribeAudioRouting()
        {
            if (!_audioRoutingSubscribed)
                return;
            _audioRoutingSubscribed = false;
            try
            {
                Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault().AudioEndpointChanged -= OnAudioEndpointChanged;
            }
            catch (Exception)
            {
            }
        }

        private void OnAudioEndpointChanged(Windows.Phone.Media.Devices.AudioRoutingManager sender, object args)
        {
            if (_inAppClosing)
                return;

            try
            {
                var endpoint = (int)sender.GetAudioEndpoint();
                if (endpoint == _lastAudioEndpoint)
                    return;
                _lastAudioEndpoint = endpoint;

                VoiceDiagnostics.Log($"Маршрут звука изменился (гарнитура/динамик): {VoiceDiagnostics.CurrentRouting()}");
                AudioRoutingChanged?.Invoke(this, EventArgs.Empty);

                var client = _inAppClient;
                if (client != null && !_inAppClosing)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (!_inAppClosing)
                                client.UpdateAudioDevices();
                        }
                        catch (Exception ex)
                        {
                            VoiceDiagnostics.Log("UpdateAudioDevices после смены маршрута", ex);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Событие смены маршрута звука", ex);
            }
        }

        // In call mode the phone plays through the earpiece; switch to the loudspeaker unless
        // a wired or Bluetooth headset is in use.
        private static void RouteToLoudspeakerIfNoHeadset()
        {
            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Phone.Media.Devices.AudioRoutingManager"))
                return;

            try
            {
                var routing = Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault();
                var current = routing.GetAudioEndpoint();
                if (current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.WiredHeadset ||
                    current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.WiredHeadsetSpeakerOnly ||
                    current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.Bluetooth ||
                    current == Windows.Phone.Media.Devices.AudioRoutingEndpoint.BluetoothWithNoiseAndEchoCancellation ||
                    routing.AvailableAudioEndpoints.HasFlag(Windows.Phone.Media.Devices.AvailableAudioRoutingEndpoints.Bluetooth))
                    return;

                routing.SetAudioEndpoint(Windows.Phone.Media.Devices.AudioRoutingEndpoint.Speakerphone);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private async void OnInAppCallEndRequested(VoipPhoneCall sender, CallStateChangeEventArgs args)
        {
            try
            {
                // The user ended the call from the system call UI.
                VoiceDiagnostics.Log("Система запросила завершение VoIP-звонка");
                await DisconnectAsync();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Завершение из системного экрана звонка", ex);
                Logger.LogError(ex);
            }
        }

        private async Task RequestBackgroundExecutionAsync()
        {
            // Prefer ExtendedExecutionForegroundSession(Unconstrained): on Windows 10 Mobile 15254
            // it keeps the app running behind the lock screen during a voice call without suspending.
            var foregroundAllowed = false;
            if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundSession"))
            {
                try
                {
                    var fg = new Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundSession
                    {
                        Reason = Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundReason.Unconstrained,
                        Description = "Голосовой звонок Discord"
                    };
                    fg.Revoked += (s, a) =>
                    {
                        VoiceDiagnostics.Log($"Неограниченная работа в фоне ОТОЗВАНА системой: {a.Reason}");
                        VoiceDiagnostics.Flush();
                    };
                    var fgResult = await fg.RequestExtensionAsync();
                    VoiceDiagnostics.Log($"Неограниченная работа в фоне (экран блокировки): {fgResult}");
                    if (fgResult == Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundResult.Allowed)
                    {
                        lock (_keepAliveLock)
                        {
                            if (_inAppClosing)
                                fg.Dispose();
                            else
                                _foregroundSession = fg;
                        }
                        foregroundAllowed = true;
                    }
                    else
                    {
                        fg.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Неограниченная работа в фоне", ex);
                    Logger.LogError(ex);
                }
            }

            if (foregroundAllowed)
                return;

            try
            {
                var session = new Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession
                {
                    Reason = Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionReason.Unspecified,
                    Description = "Голосовой звонок Discord"
                };
                session.Revoked += (s, a) =>
                {
                    VoiceDiagnostics.Log($"Работа в фоне ОТОЗВАНА системой: {a.Reason}");
                    VoiceDiagnostics.Flush();
                };
                var result = await session.RequestExtensionAsync();
                VoiceDiagnostics.Log($"Работа в фоне (свёрнутое приложение): {result}");
                if (result == Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionResult.Allowed)
                {
                    lock (_keepAliveLock)
                    {
                        if (_inAppClosing)
                            session.Dispose();
                        else
                            _backgroundSession = session;
                    }
                }
                else
                {
                    session.Dispose();
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Работа в фоне", ex);
                Logger.LogError(ex);
            }
        }

        // Raw WASAPI inside WebRTC does not register as background media playback with UWP PLM.
        // A silent looping Communications MediaPlayer (all-zero PCM, non-zero volume so the OS does not
        // optimise the session away) keeps uap3:backgroundMediaPlayback active behind the lock screen.
        private void StartBackgroundAudioKeepAlive()
        {
            _ = Task.Run(() =>
            {
                if (_inAppClosing)
                    return;

                InMemoryRandomAccessStream stream = null;
                MediaPlayer player = null;
                try
                {
                    var wav = CreateSilentWav(sampleRate: 48000, durationMs: 500);
                    stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(wav);
                        writer.StoreAsync().AsTask().Wait();
                        writer.DetachStream();
                    }

                    player = new MediaPlayer
                    {
                        AudioCategory = MediaPlayerAudioCategory.Communications,
                        IsLoopingEnabled = true,
                        AutoPlay = true,
                        Volume = 0.01,
                    };
                    try { player.CommandManager.IsEnabled = false; } catch (Exception) { }
                    player.Source = MediaSource.CreateFromStream(stream, "audio/wav");

                    lock (_keepAliveLock)
                    {
                        if (_inAppClosing)
                        {
                            try { player.Pause(); player.Source = null; player.Dispose(); } catch (Exception) { }
                            try { stream.Dispose(); } catch (Exception) { }
                            return;
                        }
                        _keepAlivePlayer = player;
                        _keepAliveStream = stream;
                    }

                    player.Play();
                    VoiceDiagnostics.Log("Фоновая аудиосессия (экран блокировки): запущена");
                }
                catch (Exception ex)
                {
                    try { player?.Dispose(); } catch (Exception) { }
                    try { stream?.Dispose(); } catch (Exception) { }
                    VoiceDiagnostics.Log("Фоновая аудиосессия", ex);
                    Logger.LogError(ex);
                }
            });
        }

        private static byte[] CreateSilentWav(int sampleRate, int durationMs)
        {
            var samples = sampleRate * durationMs / 1000;
            var dataBytes = samples * 2;
            using (var ms = new MemoryStream(44 + dataBytes))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + dataBytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);             // PCM
                w.Write((short)1);             // mono
                w.Write(sampleRate);
                w.Write(sampleRate * 2);       // byte rate
                w.Write((short)2);             // block align
                w.Write((short)16);            // bits per sample
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(dataBytes);
                w.Write(new byte[dataBytes]);  // silence
                w.Flush();
                return ms.ToArray();
            }
        }

        private void StopBackgroundExecution()
        {
            Windows.ApplicationModel.ExtendedExecution.Foreground.ExtendedExecutionForegroundSession fg;
            Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession bg;
            MediaPlayer player;
            InMemoryRandomAccessStream stream;

            lock (_keepAliveLock)
            {
                fg = _foregroundSession;
                _foregroundSession = null;
                bg = _backgroundSession;
                _backgroundSession = null;
                player = _keepAlivePlayer;
                _keepAlivePlayer = null;
                stream = _keepAliveStream;
                _keepAliveStream = null;
            }

            if (player != null)
            {
                try { player.Pause(); } catch (Exception) { }
                try { player.Source = null; } catch (Exception) { }
                try { player.Dispose(); } catch (Exception) { }
            }
            try { stream?.Dispose(); } catch (Exception) { }
            try { fg?.Dispose(); } catch (Exception) { }
            try { bg?.Dispose(); } catch (Exception) { }
        }

        // Join/leave chimes for other people in our channel.
        private Task OnOtherVoiceStateUpdated(DiscordClient client, VoiceStateUpdateEventArgs e)
        {
            if (e.User == null || e.User == DiscordManager.Discord.CurrentUser)
                return Task.CompletedTask;

            var before = e.Before?.ChannelId ?? e.Before?.Channel?.Id;
            var after = e.After?.ChannelId ?? e.After?.Channel?.Id;
            if (before == after)
                return Task.CompletedTask;

            if (after == Channel.Id)
                VoiceSounds.Play(VoiceSounds.Kind.UserJoined);
            else if (before == Channel.Id)
            {
                VoiceSounds.Play(VoiceSounds.Kind.UserLeft);
                VoiceSpeakingState.Set(e.User.Id, false);
            }

            return Task.CompletedTask;
        }

        private async Task CloseInAppClientAsync()
        {
            _inAppClosing = true;
            UnsubscribeAudioRouting();
            _voiceStateUpdateCompletion.TrySetCanceled();
            _voiceServerUpdateCompletion.TrySetCanceled();
            if (Interlocked.Exchange(ref _inAppCallCounted, 0) == 1)
                Interlocked.Decrement(ref _activeInAppCalls);

            discord.VoiceStateUpdated -= OnVoiceStateUpdated;
            discord.VoiceServerUpdated -= OnVoiceServerUpdated;
            discord.VoiceStateUpdated -= OnOtherVoiceStateUpdated;
            VoiceSpeakingState.Clear();

            // 1. Dispose the native VoiceClient FIRST on a background thread (never on the UI thread,
            //    and BEFORE NotifyCallEnded() — ending the VoIP call switches audio endpoints and makes
            //    AudioDeviceWasapi's observer thread restart WASAPI concurrently with teardown).
            VoiceClient client = null;
            var lockTaken = false;
            try
            {
                lockTaken = await _inAppLock.WaitAsync(3000).ConfigureAwait(false);
                client = _inAppClient;
                _inAppClient = null;
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Ожидание блокировки голоса при закрытии", ex);
                client = Interlocked.Exchange(ref _inAppClient, null);
            }
            finally
            {
                if (lockTaken)
                    _inAppLock.Release();
            }

            if (client != null)
            {
                VoiceDiagnostics.Log("Закрытие голосового модуля…");
                VoiceDiagnostics.Flush();
                var disposeTask = Task.Run(() =>
                {
                    try
                    {
                        client.Dispose();
                        VoiceDiagnostics.Log("Голосовой модуль закрыт");
                    }
                    catch (Exception ex)
                    {
                        VoiceDiagnostics.Log("Закрытие голосового модуля", ex);
                        Logger.LogError(ex);
                    }
                });
                var finished = await Task.WhenAny(disposeTask, Task.Delay(5000)).ConfigureAwait(false);
                if (finished != disposeTask)
                {
                    VoiceDiagnostics.Log("ВНИМАНИЕ: таймаут закрытия голосового модуля (5 с)");
                    VoiceDiagnostics.Flush();
                }
            }

            // 2. Play the leave chime while still in call mode, then end the system VoIP call.
            if (_joinSoundPlayed)
            {
                _joinSoundPlayed = false;
                VoiceSounds.Play(VoiceSounds.Kind.SelfLeft);
            }

            var call = Interlocked.Exchange(ref _inAppCall, null);
            if (call != null)
            {
                try
                {
                    call.EndRequested -= OnInAppCallEndRequested;
                    call.NotifyCallEnded();
                    VoiceDiagnostics.Log("VoIP-звонок завершён");
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("NotifyCallEnded", ex);
                    Logger.LogError(ex);
                }
            }

            // 3. Release background execution sessions, silent keep-alive audio, and display lock.
            StopBackgroundExecution();

            var display = Interlocked.Exchange(ref _displayRequest, null);
            if (display != null)
            {
                syncContext.Post(_ =>
                {
                    try { display.RequestRelease(); } catch (Exception) { }
                }, null);
            }
        }

        private async Task<VoipPhoneCallResourceReservationStatus> ReserveCallResourcesAsync()
        {
            try
            {
                return await _voipCallCoordinator.ReserveCallResourcesAsync("Unicord.Universal.Voice.Background.VoiceBackgroundTask");
            }
            catch (Exception ex)
            {
                // ERROR_ALREADY_EXISTS
                if (ex.HResult == -2147024713)
                    return VoipPhoneCallResourceReservationStatus.Success;
                else
                    throw;
            }
        }

        private VoiceState _appliedState = VoiceState.None;

        /// <summary>Pushes the current mute/deafen state to the voice module and to Discord (once per change).</summary>
        private async Task ApplyMuteStateAsync()
        {
            var state = _state;
            if (state == _appliedState)
                return;
            _appliedState = state;

            var muted = state != VoiceState.None;
            var deafened = state.HasFlag(VoiceState.Deafened);

            var client = _inAppClient;
            if (client != null)
            {
                // Native setters are atomic/lock-free (deafen drops incoming audio in ProcessRawPacket
                // without tearing down WASAPI playout, so un-deafening resumes instantaneously).
                VoiceDiagnostics.Log($"Микрофон {(muted ? "выкл" : "вкл")}, звук {(deafened ? "выкл" : "вкл")}");
                client.Deafened = deafened;
                client.Muted = muted;
                await SendVoiceStateUpdateAsync(state, Channel.Id);
                return;
            }

            if (_appServiceConnected)
            {
                await SendRequestAsync(new ValueSet() { ["req"] = (uint)VoiceServiceRequest.DeafenRequest, ["deafened"] = deafened });
                await SendRequestAsync(new ValueSet() { ["req"] = (uint)VoiceServiceRequest.MuteRequest, ["muted"] = muted });
            }
        }

        public Task DisconnectAsync()
        {
            if (_inApp)
            {
                lock (_disconnectGate)
                {
                    if (_disconnectTask == null)
                        _disconnectTask = DisconnectInAppCoreAsync();
                    return _disconnectTask;
                }
            }

            return DisconnectAppServiceAsync();
        }

        private async Task DisconnectInAppCoreAsync()
        {
            VoiceDiagnostics.Log("Отключение от голоса: начало");
            VoiceDiagnostics.Flush();

            ConnectionStatus = _strings.GetString("DisconnectedState");
            try
            {
                Disconnected?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }

            var gatewayLeave = SendVoiceStateUpdateAsync(VoiceState.None, null);
            await CloseInAppClientAsync().ConfigureAwait(false);
            await gatewayLeave.ConfigureAwait(false);

            VoiceDiagnostics.Log("Отключение от голоса: завершено");
            VoiceDiagnostics.Flush();
        }

        private async Task DisconnectAppServiceAsync()
        {
            _pcClosing = true;
            discord.VoiceStateUpdated -= OnVoiceStateUpdated;
            discord.VoiceServerUpdated -= OnVoiceServerUpdated;
            discord.VoiceStateUpdated -= OnOtherVoiceStateUpdated;

            var set = new ValueSet() { ["req"] = (uint)VoiceServiceRequest.DisconnectRequest };
            await SendRequestAsync(set);
        }

        private async Task<ValueSet> SendRequestAsync(ValueSet request)
        {
            var response = await _appServiceConnection.SendMessageAsync(request);
            if (response.Message != null)
            {
                // double cast because C#
                if ((VoiceServiceRequest)(uint)response.Message["req"] == VoiceServiceRequest.RequestFailed)
                {
                    throw new Exception((string)response.Message["msg"]);
                }

                return response.Message;
            }

            return null;
        }

        private async Task SendVoiceStateUpdateAsync(VoiceState state, ulong? channel_id)
        {
            try
            {
                var payload = new VoiceStateUpdatePayload
                {
                    GuildId = Channel.Guild?.Id,
                    ChannelId = channel_id,
                    Deafened = channel_id != null ? state.HasFlag(VoiceState.Deafened) : false,
                    Muted = channel_id != null ? (state != VoiceState.None) : false
                };

#pragma warning disable CS0618 // Type or member is obsolete
                await DiscordManager.Discord.SendPayloadAsync(GatewayOpCode.VoiceStateUpdate, payload);
#pragma warning restore CS0618 // Type or member is obsolete
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
            }
        }

        private async Task RingDmCallIfNeededAsync()
        {
            if (Channel == null || Channel.Guild != null)
                return;

            try
            {
                // Subscribe this Gateway session to CALL_CREATE / CALL_UPDATE / CALL_DELETE / peer VOICE_STATE_UPDATE
                await discord.RequestCallConnectAsync(Channel.Id).ConfigureAwait(false);
                VoiceDiagnostics.Log("ЛС-звонок: отправлен запрос подписки на звонок (OP 13 CALL_CONNECT)");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("ЛС-звонок: OP 13 CALL_CONNECT", ex);
            }

            try
            {
                if (Channel.Type == ChannelType.Private &&
                    discord.PrivateVoiceStates != null &&
                    discord.PrivateVoiceStates.Values.Any(v => (v.ChannelId == Channel.Id || v.Channel?.Id == Channel.Id) && v.User?.Id != discord.CurrentUser?.Id))
                {
                    VoiceDiagnostics.Log("ЛС-звонок: собеседник уже в звонке, присоединяемся");
                    return;
                }

                await discord.RingCallAsync(Channel.Id).ConfigureAwait(false);
                VoiceDiagnostics.Log("ЛС-звонок: вызов отправлен собеседнику (ring)");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("ЛС-звонок: отправка вызова (ring)", ex);
            }
        }

        private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            if (args.Request.Message.TryGetValue("ev", out var ev))
            {
                var serviceEvent = (VoiceServiceEvent)(uint)ev;
                switch (serviceEvent)
                {
                    case VoiceServiceEvent.Connected:
                        ConnectionStatus = string.Format(_strings.GetString("ConnectedStateFormat"), ChannelDisplayName);
                        if (!_joinSoundPlayed)
                        {
                            _joinSoundPlayed = true;
                            VoiceSounds.Play(VoiceSounds.Kind.SelfJoined);
                        }
                        break;
                    case VoiceServiceEvent.Speaking:
                        if (args.Request.Message.TryGetValue("speaking", out var speaking))
                            VoiceSpeakingState.SetFromNative(speaking as string);
                        break;
                    case VoiceServiceEvent.Reconnecting:
                        ConnectionStatus = _strings.GetString("ReconnectingState");
                        break;
                    case VoiceServiceEvent.Disconnected:
                        var closeCode = args.Request.Message.TryGetValue("code", out var rawCode) && rawCode is uint c ? c : 0u;
                        var isFinal = closeCode == 0 || closeCode == 4004 || closeCode == 4014 || closeCode == 4017 || closeCode == 4021 || closeCode == 4022;
                        if (!_pcClosing && !isFinal && _pcRejoins < 3)
                        {
                            _pcRejoins++;
                            ConnectionStatus = _strings.GetString("ReconnectingState");
                            VoiceDiagnostics.Log($"ПК-голос: закрыто с кодом {closeCode}, запрос новой сессии ({_pcRejoins} из 3)");
                            await SendVoiceStateUpdateAsync(_state, Channel.Id);
                            break;
                        }

                        _pcClosing = true;
                        ConnectionStatus = _strings.GetString("DisconnectedState");
                        discord.VoiceStateUpdated -= OnVoiceStateUpdated;
                        discord.VoiceServerUpdated -= OnVoiceServerUpdated;
                        discord.VoiceStateUpdated -= OnOtherVoiceStateUpdated;
                        VoiceSpeakingState.Clear();
                        if (_joinSoundPlayed)
                        {
                            _joinSoundPlayed = false;
                            VoiceSounds.Play(VoiceSounds.Kind.SelfLeft);
                        }
                        Disconnected?.Invoke(this, null);
                        await SendVoiceStateUpdateAsync(VoiceState.None, null);
                        break;
                    case VoiceServiceEvent.Muted:
                        await SendVoiceStateUpdateAsync(_state, Channel.Id);
                        break;
                    case VoiceServiceEvent.Deafened:
                        await SendVoiceStateUpdateAsync(_state, Channel.Id);
                        break;
                    case VoiceServiceEvent.UdpPing:
                        if (_pcHealthySinceUtc == DateTime.MinValue)
                            _pcHealthySinceUtc = DateTime.UtcNow;
                        else if (!_pcStabilityResetDone && (DateTime.UtcNow - _pcHealthySinceUtc).TotalSeconds >= 30)
                        {
                            _pcStabilityResetDone = true;
                            _pcStarts = 0;
                            _pcRejoins = 0;
                        }
                        UdpPing = (uint)args.Request.Message["ping"];
                        break;
                    case VoiceServiceEvent.WebSocketPing:
                        WebSocketPing = (uint)args.Request.Message["ping"];
                        break;
                    default:
                        break;
                }
            }
        }

        private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            _appServiceConnected = false;
        }

        private Task OnVoiceStateUpdated(DiscordClient client, VoiceStateUpdateEventArgs e)
        {
            if ((e.Channel == Channel || e.Channel?.Id == Channel.Id) && e.User?.Id == DiscordManager.Discord.CurrentUser?.Id)
            {
                // The same account may be in voice on another device: its voice state updates arrive
                // here too, but with that device's session id. Using it makes the voice server reply
                // 4006 (session no longer valid), so only accept updates for this gateway session.
                var own = DiscordManager.Discord.GatewaySessionId;
                if (_inApp)
                    VoiceDiagnostics.Log($"Discord: состояние голоса, сессия {Prefix(e.SessionId)}, сессия шлюза {Prefix(own)}, " +
                                         $"совпадают={(e.SessionId == own ? "да" : "НЕТ")}, mute={e.After?.IsSelfMuted}, deaf={e.After?.IsSelfDeafened}");
                if (!string.IsNullOrEmpty(own) && e.SessionId != own)
                {
                    if (_inApp)
                        VoiceDiagnostics.Log("Discord: состояние голоса ДРУГОЙ сессии этого аккаунта (другое устройство) — пропущено");
                    return Task.CompletedTask;
                }

                if (!_inApp)
                {
                    var pcChanged = _pcSessionId != null && _pcSessionId != e.SessionId;
                    _pcSessionId = e.SessionId;
                    _voiceStateUpdateCompletion.TrySetResult(e);
                    if (pcChanged && _pcToken != null && _pcEndpoint != null && _appServiceConnected)
                        _ = Task.Run(() => SendPcConnectRequestAsync("сменилась сессия"));
                    return Task.CompletedTask;
                }

                // In-app voice keeps listening: the session id may change while we are connected.
                var changed = _inAppSessionId != null && _inAppSessionId != e.SessionId;
                VoiceDiagnostics.Log(_inAppSessionId == null ? "Discord: состояние голоса получено (сессия есть)"
                                     : changed ? "Discord: сессия голоса СМЕНИЛАСЬ" : "Discord: состояние голоса обновлено");
                _inAppSessionId = e.SessionId;
                _voiceStateUpdateCompletion.TrySetResult(e);
                if (changed && _inAppToken != null && _inAppClient != null)
                    _ = RestartNativeClientSafeAsync("сменилась сессия");
            }

            return Task.CompletedTask;
        }

        private Task OnVoiceServerUpdated(DiscordClient client, VoiceServerUpdateEventArgs e)
        {
            var matches = Channel.Guild != null
                ? e.Guild == Channel.Guild
                : (e.Guild == null && e.ChannelId == Channel.Id);
            if (matches)
            {
                if (!_inApp)
                {
                    if (string.IsNullOrEmpty(e.Endpoint))
                        return Task.CompletedTask;

                    var hadEndpoint = _pcEndpoint != null;
                    _pcEndpoint = e.Endpoint;
                    _pcToken = e.VoiceToken;

                    if (!_voiceServerUpdateCompletion.Task.IsCompleted)
                        _voiceServerUpdateCompletion.TrySetResult(e);
                    else if (hadEndpoint && _appServiceConnected)
                        _ = Task.Run(() => SendPcConnectRequestAsync("новый голосовой сервер"));
                    return Task.CompletedTask;
                }

                // Only the host name is logged, never the token.
                if (string.IsNullOrEmpty(e.Endpoint))
                {
                    VoiceDiagnostics.Log("Discord: голосовой сервер освобождён, ждём новый");
                    return Task.CompletedTask;
                }

                var first = _inAppToken == null && _inAppEndpoint == null;
                VoiceDiagnostics.Log($"Discord выдал голосовой сервер: {e.Endpoint.Split(':')[0]}{(first ? "" : " (повторно)")}");
                var hadClient = _inAppClient != null || _inAppEndpoint != null;
                _inAppEndpoint = e.Endpoint;
                _inAppToken = e.VoiceToken;

                if (!_voiceServerUpdateCompletion.Task.IsCompleted)
                    _voiceServerUpdateCompletion.TrySetResult(e);
                else if (hadClient)
                    _ = RestartNativeClientSafeAsync("новый голосовой сервер");
            }

            return Task.CompletedTask;
        }

        // First 4 characters and length only: enough to compare session ids in a shareable report.
        private static string Prefix(string id) =>
            string.IsNullOrEmpty(id) ? "(нет)" : (id.Length <= 4 ? id : id.Substring(0, 4)) + $"…({id.Length})";

        private async Task RestartNativeClientSafeAsync(string reason)
        {
            try
            {
                await StartNativeClientAsync(reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log($"Перезапуск голоса ({reason})", ex);
                Logger.LogError(ex);
            }
        }
    }

    public enum CallAudioRoute
    {
        Speakerphone,
        Earpiece,
        Headset,
        Bluetooth
    }

    [Flags]
    public enum VoiceState
    {
        None = 0,
        Deafened = 1,
        Muted = 2
    }
}