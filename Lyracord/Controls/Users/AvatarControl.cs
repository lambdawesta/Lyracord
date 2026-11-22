using System;
using Unicord.Universal.Models.User;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Templated Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234235

namespace Unicord.Universal.Controls.Users
{
    public sealed class AvatarControl : Control
    {
        public Uri Source
        {
            get { return (Uri)GetValue(SourceProperty); }
            set { SetValue(SourceProperty, value); }
        }

        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register("Source", typeof(Uri), typeof(AvatarControl), new PropertyMetadata(null, OnSourceChanged));

        public PresenceViewModel Presence
        {
            get { return (PresenceViewModel)GetValue(PresenceProperty); }
            set { SetValue(PresenceProperty, value); }
        }

        public static readonly DependencyProperty PresenceProperty =
            DependencyProperty.Register("Presence", typeof(PresenceViewModel), typeof(AvatarControl), new PropertyMetadata(null));

        public AvatarControl()
        {
            this.DefaultStyleKey = typeof(AvatarControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            UpdateAvatarImage();
        }

        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AvatarControl control)
                control.UpdateAvatarImage();
        }

        private void UpdateAvatarImage()
        {
            var shape = (GetTemplateChild("ImageEllipse") ?? GetTemplateChild("ImagePath")) as Windows.UI.Xaml.Shapes.Shape;
            if (shape?.Fill is Windows.UI.Xaml.Media.ImageBrush brush)
            {
                if (Source == null)
                {
                    brush.ImageSource = null;
                }
                else
                {
                    var width = !double.IsNaN(Width) && Width > 0 ? (int)Width : 64;
                    brush.ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(Source)
                    {
                        DecodePixelWidth = width,
                        DecodePixelType = Windows.UI.Xaml.Media.Imaging.DecodePixelType.Logical
                    };
                }
            }
        }
    }
}
