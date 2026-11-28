//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Controls;

namespace Telegram.Views.Supergroups.Popups
{
    public sealed partial class SupergroupBroadcastGroupPopup : ModalPopup
    {
        public SupergroupBroadcastGroupPopup()
        {
            InitializeComponent();

            PrimaryButtonText = Strings.GigagroupConvertProcessButton;
            SecondaryButtonText = Strings.GigagroupConvertCancelButton;
            ButtonsLayout = ModalPopupButtonsLayout.Vertical;
        }
    }
}
