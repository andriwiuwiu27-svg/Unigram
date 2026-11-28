//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Services;

namespace Telegram.Views.Wallet.Popups
{
    public sealed partial class WalletFirstGramsPopup : ModalPopup
    {
        public WalletFirstGramsPopup(IClientService clientService)
        {
            InitializeComponent();

            PrimaryButtonText = Strings.WalletGotIt;
            ButtonsLayout = ModalPopupButtonsLayout.Vertical;

            // The option prices a million Grams, so one Gram is a millionth of it.
            var rate = clientService.Options.MillionGramToUsdRate;
            Subtitle.Text = rate > 0
                ? string.Format(Strings.WalletFirstGramsRate, GiftHelper.FormatGrams(1_000_000_000), (rate / 1e6).ToString("F2"))
                : Strings.WalletFirstGramsActions;
        }
    }
}
