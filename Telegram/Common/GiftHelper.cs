//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Telegram.Collections;
using Telegram.Controls;
using Telegram.Controls.Cells;
using Telegram.Converters;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.Views.Popups;
using Telegram.Views.Premium.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Common
{
    public static class GiftHelper
    {
        private const long NanogramsPerGram = 1_000_000_000;

        private static readonly BigInteger UnitsPerCent = BigInteger.Pow(10, WalletHelper.CurrencyDecimals - 2);

        /// <summary>
        /// An on-chain amount as the gift market writes it, without its unit: whole Grams grouped,
        /// two decimals otherwise, and four below a hundredth so that a small amount doesn't read as 0.
        /// </summary>
        public static string FormatGramValue(long nanograms)
        {
            if (nanograms % NanogramsPerGram == 0)
            {
                return (nanograms / NanogramsPerGram).ToString("N0");
            }

            var grams = nanograms / (double)NanogramsPerGram;
            return grams.ToString(Math.Abs(grams) >= 0.01 ? "0.##" : "0.####");
        }

        /// <summary>
        /// The same with its unit, "12 Gram", held together so that it never wraps inside a sentence.
        /// </summary>
        public static string FormatGrams(long nanograms)
        {
            return NoBreak(string.Format(Strings.GiftOnchainGramAmount, FormatGramValue(nanograms)));
        }

        public static string NoBreak(string text)
        {
            return text.Replace(' ', ' ');
        }

        /// <summary>
        /// "~$12.34", rounded to the cent; null while there is no rate or when it is worth no cent.
        /// </summary>
        public static string FormatUsd(IClientService clientService, long nanograms)
        {
            if (!WalletHelper.TryToUsd(clientService, nanograms, out var units))
            {
                return null;
            }

            var cents = (long)((units + UnitsPerCent / 2) / UnitsPerCent);
            return cents > 0
                ? "~" + Formatter.FormatAmount(cents, "USD")
                : null;
        }

        /// <summary>
        /// True when the gift's resale price is shown in Grams: an on-chain sale takes nothing else.
        /// </summary>
        public static bool IsResaleInGrams(UpgradedGift gift)
        {
            return gift.ResaleParameters is GiftResaleParameters parameters && (parameters.GramOnly || gift.UsesOnChainResale);
        }

        /// <summary>
        /// The resale price in Grams, without its unit. On-chain the nanograms are what the buyer
        /// signs for, so <paramref name="exact"/> writes every digit of them; off-chain the price is
        /// only ever whole cents.
        /// </summary>
        public static string FormatResaleGrams(UpgradedGift gift, bool exact)
        {
            var parameters = gift.ResaleParameters;
            if (gift.UsesOnChainResale && parameters.GramAmount > 0)
            {
                return exact
                    ? Formatter.GramExact(parameters.GramAmount).Join()
                    : FormatGramValue(parameters.GramAmount);
            }

            return TransferGiftPopup.FormatGram(parameters.GramCentCount);
        }

        /// <summary>
        /// Picks whom to send a gift to, the current user included, and opens the gift popup for them.
        /// </summary>
        public static async void ChooseRecipient(IClientService clientService, INavigationService navigationService)
        {
            var popup = ChooseChatsPopup.Create(navigationService, Strings.GiftTelegramPremiumOrStarsTitle);

            if (clientService.TryGetUser(clientService.Options.MyId, out User self))
            {
                var selfChat = await clientService.SendAsync(new CreatePrivateChat(self.Id, false)) as Chat;

                var profile = new ProfileCell();
                profile.UpdateUser(clientService, self, 36, true);
                profile.Subtitle = Strings.Gift2Myself;

                var button = new Button
                {
                    Content = profile,
                    Style = BootStrapper.Current.Resources["ListEmptyButtonStyle"] as Style,
                    Margin = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(4)
                };

                button.Click += new SelfRecipient(popup, selfChat).OnClick;
                popup.Header = button;
            }

            var options = new ChooseChatsOptions()
            {
                AllowChannelChats = false,
                AllowGroupChats = false,
                AllowBotChats = false,
                AllowUserChats = true,
                AllowSecretChats = false,
                AllowSelf = false,
                CanPostMessages = false,
                CanInviteUsers = false,
                CanShareContact = false,
                Mode = ChooseChatsMode.Chats,
                ShowMessages = false
            };

            var confirm = await popup.PickAsync(navigationService.XamlRoot, Array.Empty<long>(), options, ListViewSelectionMode.None);
            if (confirm != ModalPopupResult.Primary)
            {
                return;
            }

            var chat = popup.ViewModel.SelectedItems.FirstOrDefault();
            var user = clientService.GetUser(chat);

            if (user != null)
            {
                clientService.TryGetUserFull(user.Id, out UserFullInfo fullInfo);
                fullInfo ??= await clientService.SendAsync(new GetUserFullInfo(user.Id)) as UserFullInfo;

                if (fullInfo != null)
                {
                    navigationService.ShowPopup(new GiftPopup(clientService, navigationService, user, fullInfo));
                }
            }
        }

        // The button lives inside the popup and dies with it, so the handler needs no detaching.
        private sealed class SelfRecipient
        {
            private readonly ChooseChatsPopup _popup;
            private readonly Chat _chat;

            public SelfRecipient(ChooseChatsPopup popup, Chat chat)
            {
                _popup = popup;
                _chat = chat;
            }

            public void OnClick(object sender, RoutedEventArgs e)
            {
                _popup.ViewModel.SelectedItems = new RangeObservableCollection<Chat> { _chat };
                _popup.Hide(ModalPopupResult.Primary);
            }
        }

        /// <summary>
        /// Takes an upgraded gift to the TON blockchain: into the current user's wallet when there
        /// can be one, through Fragment otherwise.
        /// </summary>
        public static async void Export(IClientService clientService, INavigationService navigation, ReceivedGift gift)
        {
            if (gift.Gift is not SentGiftUpgraded upgraded)
            {
                return;
            }

            var now = DateTime.Now.ToUnixTimeSeconds();
            if (now < gift.MintDate)
            {
                var days = (int)Math.Ceiling((gift.MintDate - now) / 86400d);
                await navigation.ShowPopupAsync(Locale.Declension(Strings.R.Gift2ExportTONUnlocksAlertText, days), Strings.Gift2ExportTONUnlocksAlertTitle, Strings.OK);
                return;
            }

            var name = upgraded.Gift.ToName();
            var wallet = clientService.Options.CanUseTonWallet;
            var keepOnProfile = false;

            if (wallet)
            {
                if (clientService.TonWalletState == null)
                {
                    await clientService.SendAsync(new LoadTonWalletState());
                }

                var popup = new MoveGiftToWalletPopup(clientService.TonWalletState?.Address);

                var confirm = await navigation.ShowPopupAsync(popup);
                if (confirm != ModalPopupResult.Primary)
                {
                    return;
                }

                keepOnProfile = popup.KeepOnProfile;
            }
            else
            {
                var confirm = await navigation.ShowPopupAsync(string.Format(Strings.Gift2ExportTONFragmentText, name), Strings.Gift2ExportTONFragmentTitle, Strings.OpenFragment, Strings.Cancel);
                if (confirm != ModalPopupResult.Primary)
                {
                    return;
                }
            }

            var message = wallet
                ? string.Format(Strings.PleaseEnterCurrentPasswordGiftWallet, name)
                : string.Format(Strings.PleaseEnterCurrentPasswordGiftTransfer, name);

            if (!await GetTemporaryPasswordAsync(clientService, navigation, message))
            {
                return;
            }

            if (wallet)
            {
                var response = await clientService.SendAsync(new MintGift(gift.ReceivedGiftId, keepOnProfile));
                if (response is Ok)
                {
                    // Kept on the profile, it is still among the received gifts.
                    if (!keepOnProfile)
                    {
                        clientService.Session.Resolve<IEventAggregator>().Publish(new UpdateGiftIsSold(gift.ReceivedGiftId));
                    }

                    navigation.ShowToast(Strings.Gift2MovedToWallet);
                }
                else if (response is Error error)
                {
                    ShowExportError(navigation, error);
                }
            }
            else
            {
                var response = await clientService.SendAsync(new GetGiftMintingUrl(gift.ReceivedGiftId));
                if (response is HttpUrl httpUrl)
                {
                    MessageHelper.OpenUrl(null, null, httpUrl.Url);
                }
                else if (response is Error error)
                {
                    ShowExportError(navigation, error);
                }
            }
        }

        // Both withdrawals take a temporary password rather than the password itself.
        private static async Task<bool> GetTemporaryPasswordAsync(IClientService clientService, INavigationService navigation, string message)
        {
            if (await clientService.SendAsync(new GetTemporaryPasswordState()) is TemporaryPasswordState { HasPassword: true, ValidFor: > 0 })
            {
                return true;
            }

            if (await clientService.SendAsync(new GetPasswordState()) is not PasswordState passwordState)
            {
                return false;
            }
            else if (!passwordState.HasPassword)
            {
                ShowSecurityCheck(navigation, true);
                return false;
            }

            var placeholder = string.IsNullOrEmpty(passwordState.PasswordHint)
                ? Strings.LoginPassword
                : passwordState.PasswordHint;

            var result = await navigation.ShowInputAsync(InputPopupType.Password, message, Strings.TwoStepVerification, placeholder, Strings.OK, Strings.Cancel);
            if (result.Result != ModalPopupResult.Primary)
            {
                return false;
            }

            var response = await clientService.SendAsync(new CreateTemporaryPassword(result.Text, 60));
            if (response is TemporaryPasswordState)
            {
                return true;
            }
            else if (response is Error error)
            {
                ShowExportError(navigation, error);
            }

            return false;
        }

        private static void ShowExportError(INavigationService navigation, Error error)
        {
            if (error.Message.Equals("PASSWORD_MISSING") || error.Message.StartsWith("PASSWORD_TOO_FRESH_") || error.Message.StartsWith("SESSION_TOO_FRESH_"))
            {
                ShowSecurityCheck(navigation, error.Message.Equals("PASSWORD_MISSING"));
            }
            else
            {
                navigation.ShowToast(error);
            }
        }

        private static async void ShowSecurityCheck(INavigationService navigation, bool passwordMissing)
        {
            var builder = new StringBuilder();
            builder.AppendLine(Strings.Gift2TransferToTONAlertText);
            builder.AppendLine($"\u2022 {Strings.Gift2TransferToTONAlertText1}");
            builder.AppendLine($"\u2022 {Strings.Gift2TransferToTONAlertText2}");

            if (passwordMissing)
            {
                var confirm = await navigation.ShowPopupAsync(builder.ToString(), Strings.Gift2TransferToTONAlertTitle, Strings.EditAdminTransferSetPassword, Strings.Cancel);
                if (confirm == ModalPopupResult.Primary)
                {
                    navigation.NavigateToPasswordSetup();
                }
            }
            else
            {
                builder.AppendLine();
                builder.AppendLine(Strings.Gift2TransferToTONAlertText3);

                await navigation.ShowPopupAsync(builder.ToString(), Strings.Gift2TransferToTONAlertTitle, Strings.OK);
            }
        }
    }
}
