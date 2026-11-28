//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Controls;
using Telegram.Controls.Messages;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Views
{
    public partial class ChatView
    {
        private DispatcherTimer _checksHintTimer;
        private ToastPopup _checksHint;

        // A second after the chat opens or a message reaches the server, while TDLib still suggests it.
        private void CheckChecksHint()
        {
            if (_viewModel == null || !_viewModel.ClientService.IsChecksHintSuggested)
            {
                return;
            }

            if (_checksHintTimer == null)
            {
                _checksHintTimer = new DispatcherTimer();
                _checksHintTimer.Interval = TimeSpan.FromSeconds(1);
                _checksHintTimer.Tick += ChecksHintTimer_Tick;
            }

            _checksHintTimer.Stop();
            _checksHintTimer.Start();
        }

        private void StopChecksHint()
        {
            _checksHintTimer?.Stop();

            if (_checksHint != null)
            {
                _checksHint.IsOpen = false;
                _checksHint = null;
            }
        }

        private void ChecksHintTimer_Tick(object sender, object e)
        {
            _checksHintTimer.Stop();
            ShowChecksHint();
        }

        private void ShowChecksHint()
        {
            if (_viewModel == null || _checksHint?.IsOpen == true || Messages.ActualHeight == 0)
            {
                return;
            }

            MessageFooter target = null;
            double targetY = 0;

            foreach (var item in _messageIdToSelector)
            {
                if (item.Value.ContentTemplateRoot is not MessageSelector { Content: MessageBubble bubble } || !_viewModel.Items.TryGetValue(item.Key, out MessageViewModel message))
                {
                    continue;
                }

                if (!message.IsOutgoing || message.SendingState != null || message.SchedulingState != null)
                {
                    continue;
                }

                var footer = bubble.MediaFooter;
                if (footer == null || footer.ActualHeight == 0)
                {
                    continue;
                }

                // The list also realizes rows off screen. The hint needs its ticks in view and 80
                // above them to sit in.
                var y = footer.TransformToVisual(Messages).TransformPoint(new Point()).Y;
                if (y <= 80 || y + footer.ActualHeight > Messages.ActualHeight)
                {
                    continue;
                }

                // The newest sent message, which is the lowest one.
                if (target == null || y > targetY)
                {
                    target = footer;
                    targetY = y;
                }
            }

            if (target == null)
            {
                return;
            }

            _checksHint = ToastPopup.ShowImpl(XamlRoot, CreateChecksHint(), null, ToastPlacementMode.TopLeft, ElementTheme.Dark, TimeSpan.FromMilliseconds(3200), target);

            if (_checksHint != null)
            {
                _viewModel.ClientService.Send(new HideSuggestedAction(new SuggestedActionViewChecksHint()));
            }
        }

        private static Grid CreateChecksHint()
        {
            var content = new Grid
            {
                ColumnSpacing = 8,
                RowSpacing = 4
            };

            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddChecksHintRow(content, 0, "ChecksSent", Strings.HintSent);
            AddChecksHintRow(content, 1, "ChecksRead", Strings.HintRead);

            return content;
        }

        private static void AddChecksHintRow(Grid content, int row, string animation, string text)
        {
            var icon = new AnimatedImage
            {
                Source = new LocalFileSource($"ms-appx:///Assets/Animations/{animation}.tgs"),
                Width = 24,
                Height = 24,
                AutoPlay = true,
                LoopCount = 1,
                IsCachingEnabled = false,
                FrameSize = new Size(24, 24),
                DecodeFrameType = DecodePixelType.Logical
            };

            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetRow(icon, row);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 1);

            content.Children.Add(icon);
            content.Children.Add(label);
        }
    }
}
