using System.Linq;
using Unicord.Universal.Models.User;
using Unicord.Universal.Services;

namespace Unicord.Universal.Commands.Users
{
    internal class SendMessageCommand : DiscordCommand<UserViewModel>
    {
        public SendMessageCommand(UserViewModel viewModel) : base(viewModel)
        {
        }

        public override bool CanExecute(object parameter)
        {
            return viewModel != null && discord != null && !viewModel.IsCurrent;
        }

        public override async void Execute(object parameter)
        {
            if (viewModel == null || discord == null)
                return;

            var channel = discord.PrivateChannels.Values
                .Where(c => c.Type == DSharpPlus.ChannelType.Private && c.Recipients != null && c.Recipients.Count == 1)
                .FirstOrDefault(c => c.Recipients[0]?.Id == viewModel.Id);

            if (channel == null)
            {
                try
                {
                    channel = await discord.CreateDmChannelAsync(viewModel.Id);
                }
                catch (System.Exception ex)
                {
                    VoiceDiagnostics.Log("ЛС: создание канала", ex);
                    return;
                }
            }

            if (channel != null)
            {
                OverlayService.GetForCurrentView().CloseOverlay();
                var service = DiscordNavigationService.GetForCurrentView();
                await service.NavigateAsync(channel);
            }
        }
    }
}
