//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Td.Api;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed partial class UnconfirmedSessionCard : UserControl, ISlidePanelChild
    {
        private readonly SlidePanel.SlideState _slide;

        public UnconfirmedSessionCard()
        {
            InitializeComponent();

            _slide = new SlidePanel.SlideState(this, false, 0);
        }

        public void Update(UnconfirmedSession session)
        {
            // TODO: multiple sessions?
            Message.Text = string.Format(Strings.UnconfirmedAuthSingle, string.Format("{0}, {1}", session.DeviceModel, session.Location));
        }

        public event RoutedEventHandler ConfirmClick
        {
            add => Confirm.Click += value;
            remove => Confirm.Click -= value;
        }

        public event RoutedEventHandler DenyClick
        {
            add => Deny.Click += value;
            remove => Deny.Click -= value;
        }

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
            Divider.Opacity = last ? 0 : 1;
        }
    }
}
