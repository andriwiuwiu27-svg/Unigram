//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Td.Api;

namespace Telegram.Views.Wallet
{
    /// <summary>
    /// The wallet card a collectible opened from the wallet can be put on, or colour.
    /// </summary>
    public interface IWalletCollectibleHost
    {
        bool IsOnCard(TonNft collectible);

        bool IsCardColor(TonNft collectible);

        /// <summary>
        /// Puts the collectible on the card, or takes it off.
        /// </summary>
        void ToggleCard(TonNft collectible);

        /// <summary>
        /// Colours the card with the collectible, putting it on the card first, or takes the colour off.
        /// </summary>
        void ToggleColor(TonNft collectible);
    }
}
