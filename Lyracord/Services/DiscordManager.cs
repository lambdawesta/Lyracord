using System;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.AsyncEvents;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Logging;
using Unicord.Universal.Dialogs;
using Unicord.Universal.Models.Messaging;
using Windows.ApplicationModel.Core;
using Windows.Security.Credentials;
using Windows.UI.Core;

namespace Unicord.Universal.Services
{
    /// <summary>Discord closed the connection with 4004: the token is wrong or expired.</summary>
    internal sealed class TokenRejectedException : Exception
    {
        public TokenRejectedException() : base("Discord rejected the token (4004).") { }
    }

    internal class DiscordManager
    {
        /// <summary>LocalSettings key: official client build number to present as (0 or missing = built-in default).</summary>
        public const string ClientBuildSetting = "ClientBuildNumber";

        private static DiscordClient _discord;
        private static ILogger<DiscordManager> _logger
            = Logger.GetLogger<DiscordManager>();

        private static readonly SemaphoreSlim _connectSemaphore
            = new SemaphoreSlim(1);
        private static TaskCompletionSource<ReadyEventArgs> _readySource
            = new TaskCompletionSource<ReadyEventArgs>();
        public static DiscordClient Discord
        {
            get => _discord;
        }

        internal static void KickoffConnectionAsync()
        {
            _ = Task.Run(async () =>
            {
                if (TryGetToken(out var token))
                    await LoginAsync(token, null, null, true);
            });
        }

        internal static async Task LoginAsync(
            string token,
            AsyncEventHandler<DiscordClient, ReadyEventArgs> onReady,
            Func<Exception, Task> onError,
            bool background,
            UserStatus status = UserStatus.Online)
        {
            await _connectSemaphore.WaitAsync();
            _readySource = new TaskCompletionSource<ReadyEventArgs>();
            try
            {
                if (Discord != null)
                {
                    try
                    {
                        var res = await _readySource.Task;
                        if (onReady != null)
                            await onReady(Discord, res);
                    }
                    catch (Exception ex)
                    {
                        if (onError != null)
                            await onError(ex);
                    }

                    return;
                }

                if (App.RoamingSettings.Read(Constants.VERIFY_LOGIN, false))
                {
                    if (background || !(await WindowsHelloManager.VerifyAsync(Constants.VERIFY_LOGIN, "VerifyLoginDisplayReason")))
                    {
                        if (onError != null)
                            await onError(null);
                        return;
                    }
                }

                try
                {
                    async Task ReadyHandler(DiscordClient sender, ReadyEventArgs e)
                    {
                        PerfTimings.Mark("READY получен, кэш заполнен");
                        // the desktop notification process cannot read the credential locker: give it an encrypted copy
                        // (Unicord kept a plain-text one in LocalSettings; TokenStore removes it)
                        _ = TokenStore.SaveForBackgroundAsync(token);
                        _ = RichPresenceService.ApplyAsync(sender);
                        sender.Ready -= ReadyHandler;
                        sender.SocketErrored -= SocketErrored;
                        sender.ClientErrored -= ClientErrored;
                        _readySource.TrySetResult(e);
                        if (onReady != null)
                        {
                            await onReady(sender, e);
                        }

                        onError = null;
                    }

                    Task SocketErrored(DiscordClient sender, SocketErrorEventArgs e)
                    {
                        sender.Ready -= ReadyHandler;
                        sender.SocketErrored -= SocketErrored;
                        sender.ClientErrored -= ClientErrored;

                        Logger.LogError(e.Exception);

                        _readySource.SetException(e.Exception);
                        return Task.CompletedTask;
                    }

                    Task ClientErrored(DiscordClient sender, ClientErrorEventArgs e)
                    {
                        sender.Ready -= ReadyHandler;
                        sender.SocketErrored -= SocketErrored;
                        sender.ClientErrored -= ClientErrored;

                        Logger.LogError(e.Exception);

                        _readySource.SetException(e.Exception);
                        return Task.CompletedTask;
                    }

                    // Close code 4004 = "authentication failed": the token is wrong or no longer valid (password
                    // changed, logged out everywhere). Since the connection no longer waits for GET /users/@me, this
                    // is how a bad token shows up; without this the app just kept "connecting" forever. Also covers a
                    // token revoked later in the session.
                    async Task SocketClosed(DiscordClient sender, SocketCloseEventArgs e)
                    {
                        if (e.CloseCode != 4004)
                            return; // other closes: DSharpPlus reconnects by itself

                        sender.Ready -= ReadyHandler;
                        sender.SocketErrored -= SocketErrored;
                        sender.ClientErrored -= ClientErrored;
                        sender.SocketClosed -= SocketClosed;

                        _logger.LogWarning("Discord rejected the token (close code 4004)");
                        try { VoiceDiagnostics.Log("Вход: Discord не принял токен (код 4004)"); } catch (Exception) { }
                        Tools.ResetPasswordVault();
                        TokenStore.Clear();

                        var error = new TokenRejectedException();
                        _readySource.TrySetException(error);
                        var handler = onError ?? App.LoginError;
                        onError = null;

                        var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
                        if (dispatcher != null)
                            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await handler(error));
                    }

                    // which official client build to present as (newer Discord features depend on it); set in
                    // Metro settings → "сборка клиента", 0 = the one built into this version
                    var build = App.LocalSettings.Read(ClientBuildSetting, 0);
                    DiscordClient.ClientBuildNumber = build > 0 ? build : DiscordClient.DefaultClientBuildNumber;

                    _discord = new DiscordClient(new DiscordConfiguration()
                    {
                        Token = token,
                        TokenType = TokenType.User,
                        LoggerFactory = Logger.LoggerFactory,
                        ReconnectIndefinitely = true
                    });

                    Discord.Ready += ReadyHandler;
                    Discord.SocketErrored += SocketErrored;
                    Discord.ClientErrored += ClientErrored;
                    Discord.SocketClosed += SocketClosed;
                    Discord.CaptchaRequested += OnDiscordCaptchaRequested;
                    Discord.AuthTokenUpdate += OnDiscordTokenUpdated;

                    DiscordClientMessenger.Register(Discord);

                    Discord.DiagnosticAdded += PerfTimings.Gateway;
                    Discord.SocketOpened += (s, e) => { PerfTimings.Mark("соединение со шлюзом открыто"); return Task.CompletedTask; };
                    PerfTimings.Mark("подключение к Discord");
                    await Discord.ConnectAsync(status: status,
                        idlesince: SystemPlatform.Desktop ? null : DateTimeOffset.Now);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failure when logging in!");
                    Tools.ResetPasswordVault();
                    _readySource.TrySetException(ex);
                    if (onError != null)
                        await onError(ex);
                }
            }
            finally
            {
                _connectSemaphore.Release();
            }
        }

        private static Task OnDiscordTokenUpdated(DiscordClient sender, AuthTokenUpdatedEventArgs args)
        {
            var vault = new PasswordVault();
            try
            {
                foreach (var c in vault.FindAllByResource(Constants.TOKEN_IDENTIFIER))
                    vault.Remove(c);
            }
            catch { }

            var newToken = new PasswordCredential(Constants.TOKEN_IDENTIFIER, "Default", args.Token);
            vault.Add(newToken);

            // ditto above about the background process (encrypted, never plain text)
            return TokenStore.SaveForBackgroundAsync(args.Token);
        }

        private static async Task OnDiscordCaptchaRequested(BaseDiscordClient sender, CaptchaRequestEventArgs args)
        {
            var tcs = new TaskCompletionSource<DiscordCaptchaResponse>();

            await CoreApplication.MainView.Dispatcher.RunAsync(CoreDispatcherPriority.High, async () =>
            {
                try
                {
                    var dialog = new CaptchaRequestDialog(args.Request);
                    await dialog.ShowAsync();
                    tcs.TrySetResult(dialog.CaptchaResponse);
                }
                catch (Exception ex)
                {
                    // no captcha possible (e.g. the dialog itself cannot be created on the phone): the request fails
                    // normally instead of waiting forever
                    try { VoiceDiagnostics.Log("Капча", ex); } catch (Exception) { }
                    tcs.TrySetResult(new DiscordCaptchaResponse());
                }
            });

            args.SetResponse(await tcs.Task);
        }

        internal static async Task LogoutAsync()
        {
            if (Discord == null) return;

            var discord = _discord;
            try
            {
                DiscordClientMessenger.Unregister(discord);
                await discord.DisconnectAsync();
                discord.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error when disposing of DiscordClient!");
            }
            finally
            {
                _discord = null;
            }
        }

        internal static bool TryGetToken(out string token)
        {
            try
            {
                var passwordVault = new PasswordVault();
                var credential = passwordVault.Retrieve(Constants.TOKEN_IDENTIFIER, "Default");
                credential.RetrievePassword();

                token = credential.Password;
                return true;
            }
            catch { }

            token = null;
            return false;
        }
    }
}
