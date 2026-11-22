using Unicord.Universal.Models.Messages;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Converters
{
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate MessageTemplate { get; set; }
        public DataTemplate SystemMessageTemplate { get; set; }

        /// <summary>Bubbles for direct messages, used only with the Metro theme (AppTheme.OLED).</summary>
        public DataTemplate DirectMessageTemplate { get; set; }

        private static bool? _metro;

        private static bool IsMetro
        {
            get
            {
                if (_metro == null)
                {
                    // the theme whose resources are loaded, so bubbles and the black Metro background always match
                    try { _metro = Themes.ThemeResources.LoadedTheme == Services.AppTheme.OLED; }
                    catch (System.Exception) { _metro = false; }
                }
                return _metro.Value;
            }
        }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is MessageViewModel message)
            {
                if (message.IsSystemMessage)
                    return SystemMessageTemplate;

                if (DirectMessageTemplate != null && IsMetro)
                    return DirectMessageTemplate;
            }

            return MessageTemplate;
        }
    }
}
