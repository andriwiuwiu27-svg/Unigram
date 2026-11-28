//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Windows.UI.Xaml;

namespace Telegram.Views.Wallet.Popups
{
    /// <summary>
    /// A collectible in the wallet that is not a Telegram gift: a username, a number, or anything
    /// else the wallet holds.
    /// </summary>
    public sealed partial class WalletCollectiblePopup : ModalPopup
    {
        private const int ImageSize = 120;

        private readonly IClientService _clientService;
        private readonly INavigationService _navigationService;
        private readonly TonNft _collectible;

        public WalletCollectiblePopup(IClientService clientService, INavigationService navigationService, TonNft collectible)
        {
            InitializeComponent();

            _clientService = clientService;
            _navigationService = navigationService;
            _collectible = collectible;

            // Decoded along its shorter side, the one UniformToFill fits to the frame.
            var image = collectible.Image ?? collectible.Thumbnail;
            var portrait = image != null && image.Width < image.Height;
            Photo.SetSource(clientService, image?.Photo, portrait ? ImageSize : 0, portrait ? 0 : ImageSize);

            Title.Text = string.IsNullOrEmpty(collectible.Name)
                ? Strings.WalletCollectible
                : collectible.Name;

            if (string.IsNullOrEmpty(collectible.Description))
            {
                Description.Visibility = Visibility.Collapsed;
            }
            else
            {
                Description.Text = collectible.Description;
            }

            foreach (var attribute in collectible.Attributes)
            {
                Attributes.Items.Add(new TableViewItem
                {
                    Header = attribute.TraitType,
                    Content = attribute.Value
                });
            }

            Attributes.Visibility = collectible.Attributes.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            PrimaryButtonText = Strings.OK;
        }

        private void Transfer_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            _ = GiftOnChainHelper.TransferCollectibleAsync(_clientService, _navigationService, _collectible);
        }

        private void Sell_Click(object sender, RoutedEventArgs e)
        {
            Hide();

            // Fragment's front page: nothing there is addressed by the item.
            MessageHelper.OpenUrl(_clientService, _navigationService, "https://fragment.com/");
        }
    }
}
