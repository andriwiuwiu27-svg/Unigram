//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls.Messages;
using Telegram.Converters;
using Telegram.Native;
using Telegram.Navigation;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels.Stories;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Telegram.Controls.Stories
{
    public sealed partial class StoryChannelInteractionBar : UserControl
    {
        private StoryViewModel _viewModel;
        public StoryViewModel ViewModel => _viewModel;

        public StoryChannelInteractionBar()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler ShareClick
        {
            add => ShareButton.Click += value;
            remove => ShareButton.Click -= value;
        }

        public void Update(StoryViewModel story)
        {
            _viewModel = story;

            if (story.ClientService.TryGetUser(story.PosterChatId, out User user) && user.Type is UserTypeBot)
            {
                Visibility = Visibility.Collapsed;
            }
            else
            {
                Visibility = Visibility.Visible;
            }

            if (story.Chat.Type is ChatTypeSupergroup)
            {
                ReplyDisabled.Visibility = Visibility.Collapsed;

                if (story.InteractionInfo != null)
                {
                    ViewersCount.Text = story.InteractionInfo.ViewCount.ToString("N0");

                    //ReactionCount.Text = story.InteractionInfo.ReactionCount.ToString("N0");
                    //ReactionCount.Visibility = story.InteractionInfo.ReactionCount > 0
                    //        ? Visibility.Visible
                    //        : Visibility.Collapsed;
                }
                else
                {
                    //Viewers.Items.Clear();
                    //Viewers.Visibility = Visibility.Collapsed;

                    //ViewersCount.Text = Strings.NobodyViews;

                    //ReactionCount.Visibility =
                    //    ReactionIcon.Visibility = Visibility.Collapsed;
                }

                ReactButton.SetReaction(story);
            }
            else
            {
                ReplyDisabled.Visibility = Visibility.Visible;

                ViewersIcon.Visibility = Visibility.Collapsed;
                ViewersCount.Visibility = Visibility.Collapsed;

                ReactButton.Visibility = Visibility.Collapsed;
            }
        }

        private void Viewers_RecentUserHeadChanged(ProfilePicture sender, MessageSender messageSender)
        {
            sender.Source = ProfilePictureSource.MessageSender(ViewModel.ClientService, messageSender);
        }
    }
}
