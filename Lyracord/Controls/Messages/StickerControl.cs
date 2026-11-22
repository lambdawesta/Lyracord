using System;
using DSharpPlus.Entities;
using Microsoft.Toolkit.Uwp.UI.Lottie;
using Microsoft.UI.Xaml.Controls;
using Unicord.Universal.Models.Messages;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Templated Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234235

namespace Unicord.Universal.Controls.Messages
{
    public sealed class StickerControl : Control
    {
        private bool _templateApplied = false;

        public StickerViewModel Sticker
        {
            get => (StickerViewModel)GetValue(StickerProperty);
            set => SetValue(StickerProperty, value);
        }

        public static readonly DependencyProperty StickerProperty =
            DependencyProperty.Register("Sticker", typeof(StickerViewModel), typeof(StickerControl), new PropertyMetadata(null, OnStickerChanged));

        public StickerControl()
        {
            this.DefaultStyleKey = typeof(StickerControl);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _templateApplied = true;
            if (Sticker != null)
                UpdateSticker(Sticker);
        }

        private static void OnStickerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is StickerViewModel s && d is StickerControl c)
            {
                c.UpdateSticker(s);
            }
        }

        private void UpdateSticker(StickerViewModel s)
        {
            if (s == null)
                return;

            if (!_templateApplied)
                _templateApplied = this.ApplyTemplate();

            var root = this.GetTemplateChild("Root") as Border;
            if (root == null)
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(s.StickerUrl) || !Uri.TryCreate(s.StickerUrl, UriKind.Absolute, out var uri))
                {
                    root.Child = new TextBlock { Text = s.Name ?? "Sticker", FontSize = 13, Opacity = 0.7 };
                    return;
                }

                if (s.Format == StickerFormat.LOTTIE)
                {
                    try
                    {
                        var player = new AnimatedVisualPlayer
                        {
                            AutoPlay = true,
                            Width = 150,
                            Height = 150
                        };

                        var source = new LottieVisualSource();
                        source.UriSource = uri;
                        player.Source = source;
                        root.Child = player;
                        return;
                    }
                    catch (Exception)
                    {
                        // Fallback below if Lottie fails on phone
                    }
                }

                // PNG, APNG, GIF (format 4), or fallback: render directly via BitmapImage without proxy query params
                root.Child = new Image
                {
                    Width = 150,
                    Height = 150,
                    Stretch = Windows.UI.Xaml.Media.Stretch.Uniform,
                    Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(uri)
                    {
                        DecodePixelWidth = 150,
                        DecodePixelHeight = 150,
                        DecodePixelType = Windows.UI.Xaml.Media.Imaging.DecodePixelType.Logical
                    }
                };
            }
            catch (Exception)
            {
                root.Child = new TextBlock { Text = s.Name ?? "Sticker", FontSize = 13, Opacity = 0.7 };
            }
        }
    }
}
