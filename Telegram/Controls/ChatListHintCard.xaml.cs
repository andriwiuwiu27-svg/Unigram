//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls.Media;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed partial class ChatListHintCard : HyperlinkButton, ISlidePanelChild
    {
        private readonly SlidePanel.SlideState _slide;

        private IClientService _clientService;

        public ChatListHintCard()
        {
            InitializeComponent();

            // The title and the subtitle wrap, so the height is only known once measured.
            _slide = new SlidePanel.SlideState(this, false, 0);
        }

        public ChatListHint Hint { get; private set; }

        public event RoutedEventHandler HideClick
        {
            add => Hide.Click += value;
            remove => Hide.Click -= value;
        }

        public void Update(IClientService clientService, ChatListHint hint)
        {
            _clientService = clientService;
            Hint = hint;

            TextBlockHelper.SetFormattedText(Title, hint.Title);
            TextBlockHelper.SetFormattedText(Subtitle, hint.Subtitle);

            Hide.Visibility = hint.CanHide
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (hint.Users.Count > 0)
            {
                FindName(nameof(Users));
                Users.Visibility = Visibility.Visible;

                Users.ItemSize = hint.Users.Count > 1 ? 24 : 36;
                Users.Items.ReplaceDiff(hint.Users);
            }
            else if (Users != null)
            {
                Users.Visibility = Visibility.Collapsed;
            }
        }

        private void Users_RecentUserHeadChanged(ProfilePicture sender, MessageSender messageSender)
        {
            if (Hint?.Type == ChatListHintType.SetProfilePhoto && messageSender is MessageSenderUser user)
            {
                sender.Source = ProfilePictureSourceText.GetGlyph(Icons.CameraAddFilled, user.UserId);
            }
            else
            {
                sender.Source = ProfilePictureSource.MessageSender(_clientService, messageSender);
            }
        }

        #region ISlidePanelChild

        public SlidePanel.SlideState SlideState => _slide;

        private bool _isFirst;
        public bool IsFirst
        {
            get => _isFirst;
            set
            {
                if (_isFirst != value)
                {
                    UpdateAttach(_isFirst = value, _isLast);
                }
            }
        }

        private bool _isLast;
        public bool IsLast
        {
            get => _isLast;
            set
            {
                if (_isLast != value)
                {
                    UpdateAttach(_isFirst, _isLast = value);
                }
            }
        }

        private double _cardRadius;
        public double CardRadius
        {
            get => _cardRadius;
            set
            {
                if (_cardRadius != value)
                {
                    _cardRadius = value;
                    UpdateAttach(_isFirst, _isLast);
                }
            }
        }

        private void UpdateAttach(bool first, bool last)
        {
            var radiusTop = first ? _cardRadius : 0;
            var radiusBottom = last ? _cardRadius : 0;

            CornerRadius = new CornerRadius(radiusTop, radiusTop, radiusBottom, radiusBottom);
            Divider.Opacity = last ? 0 : 1;
        }

        #endregion
    }
}
