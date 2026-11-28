//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed partial class FrozenAccountCard : UserControl, ISlidePanelChild
    {
        private readonly SlidePanel.SlideState _slide;

        public FrozenAccountCard()
        {
            InitializeComponent();

            _slide = new SlidePanel.SlideState(this, false, 0);
        }

        public event RoutedEventHandler Click
        {
            add => Confirm.Click += value;
            remove => Confirm.Click -= value;
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
            var radiusTop = first ? _cardRadius : 0;
            var radiusBottom = last ? _cardRadius : 0;

            // The button covers the whole row, so its pointer-over fill has to follow the card.
            Confirm.CornerRadius = new CornerRadius(radiusTop, radiusTop, radiusBottom, radiusBottom);
            Divider.Opacity = last ? 0 : 1;
        }
    }
}
