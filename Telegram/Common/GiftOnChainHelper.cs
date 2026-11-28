//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Controls;
using Telegram.Converters;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.Wallet;
using Telegram.Streams;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.Views.Stars.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Common
{
    /// <summary>
    /// Gifts bought, listed and delisted on-chain, and on-chain offers answered: TDLib prepares the
    /// wallet messages, the wallet signs them unchanged, and sendTonWalletTransaction sends them.
    /// What TDLib sends back is not the outcome: that is known once the transaction lands. Other
    /// collectibles go out the same way, from a transfer the wallet engine builds instead.
    /// </summary>
    public static class GiftOnChainHelper
    {
        // Fees paid on top of the value, and errors in the action phase ignored.
        private const int NftSendMode = 3;

        private static readonly TimeSpan RequestDebounce = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan SentHold = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan AcceptHold = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan FeeEstimateTimeout = TimeSpan.FromSeconds(5);

        // Until when an offer's buttons ignore taps, by "session:message": a request is out, a sheet
        // is open, or a transaction was just sent. Declines and accepts are held apart, because an
        // accepted offer must not be declined from the menu while its sale completes.
        private static readonly Dictionary<string, DateTime> _busy = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, DateTime> _declining = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, DateTime> _accepted = new Dictionary<string, DateTime>();

        #region Buying

        /// <summary>
        /// Buys a gift sold on-chain. True once the transaction is sent.
        /// </summary>
        public static async Task<bool> BuyAsync(IClientService clientService, INavigationService navigation, UpgradedGift gift, Chat chat, MessageSender ownerId, FormattedText text, bool isPrivate)
        {
            var xamlRoot = navigation.XamlRoot;
            var parameters = gift.ResaleParameters;

            if (parameters == null)
            {
                return false;
            }

            var price = parameters.GramAmount;

            var wallet = clientService.Session.Resolve<IWalletService>();
            var state = await wallet.RestoreAsync();

            if (!state.HasWallet)
            {
                ShowWalletUnavailable(navigation, xamlRoot, parameters.IsFixedOnChain);
                return false;
            }
            else if (state.BalanceNanograms < price)
            {
                await ShowNotEnoughAsync(navigation, xamlRoot, Strings.GiftOnchainNotEnoughToBuy, price);
                return false;
            }

            var response = await clientService.SendAsync(new GetGiftOnChainPurchaseTransaction(gift.Name, ownerId, price, text, isPrivate));
            if (response is not GiftOnChainSaleTransaction transaction || !TryGetMessages(transaction, out var regular, out var gasless))
            {
                ShowError(clientService, navigation, xamlRoot, response as Error, parameters.IsFixedOnChain);
                return false;
            }

            var isFixed = transaction is GiftOnChainSaleTransactionFixedPrice;

            // The price a Fragment sale asks for is the value of its one message, so a different
            // one is a price that changed since the gift was loaded.
            if (transaction is GiftOnChainSaleTransactionFragment fragment && fragment.GramAmount != price)
            {
                ShowDialog(xamlRoot, Strings.GiftOnchainErrorPriceChanged, false);
                return false;
            }

            var name = gift.ToName();
            var total = Total(regular);

            var recipient = ownerId.IsUser(clientService.Options.MyId)
                ? null
                : chat?.Title ?? clientService.GetTitle(ownerId);

            // Assigned before the popup exists, because the send captures them.
            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                Strings.GiftOnchainBuyConfirmTitle,
                string.Format(isFixed ? Strings.GiftOnchainBuyConfirmTextFixed : Strings.GiftOnchainBuyConfirmText, name, GiftHelper.FormatGrams(price)),
                Strings.GiftOnchainBuyConfirmButton,
                SendAsync);

            popup.AddRow(Strings.GiftOnchainRowPrice, FormatPrice(clientService, price));

            if (total > price)
            {
                popup.AddRow(Strings.GiftOnchainRowContractFee, GiftHelper.FormatGrams(total - price));
            }

            if (recipient != null)
            {
                popup.AddRow(Strings.WalletRecipient, recipient);

                if (!string.IsNullOrEmpty(text?.Text))
                {
                    popup.AddRow(Strings.Message, text.Text);
                }
            }
            else
            {
                popup.AddRow(Strings.GiftOnchainRowYouReceive, name);
            }

            AddAttributes(popup, gift);

            _ = EstimateFeeAsync(wallet, popup, regular);

            await navigation.ShowPopupAsync(popup);

            return sent;

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, gasless, Strings.GiftOnchainNotEnoughToBuy, isFixed);
                if (result == null)
                {
                    return false;
                }

                sent = true;

                var sticker = new DelayedFileSource(clientService, gift.Model.Sticker);

                ToastPopup.Show(xamlRoot, recipient != null
                    ? string.Format(Strings.GiftOnchainBuySentTo, name, recipient)
                    : string.Format(Strings.GiftOnchainBuySent, name), sticker);

                _ = ReportAsync(xamlRoot, result.Outcome,
                    recipient != null ? string.Format(Strings.GiftOnchainBoughtTo, name, recipient) : string.Format(Strings.GiftOnchainBought, name),
                    string.Format(Strings.GiftOnchainFailed, name),
                    sticker);

                return true;
            }
        }

        #endregion

        #region Deposit

        /// <summary>
        /// Sends a gift from the wallet to a Telegram account: <paramref name="ownerId"/>'s, or the
        /// current user's own. True once the transaction is sent.
        /// </summary>
        public static async Task<bool> DepositAsync(IClientService clientService, INavigationService navigation, UpgradedGift gift, MessageSender ownerId)
        {
            var xamlRoot = navigation.XamlRoot;

            var response = await clientService.SendAsync(new GetUpgradedGiftDepositInfo(ownerId, gift.Name, false));
            if (response is not UpgradedGiftDepositInfo info || string.IsNullOrEmpty(info.NftAddress))
            {
                ShowError(clientService, navigation, xamlRoot, response as Error, null);
                return false;
            }

            // What the message carries beyond the fees comes back to the wallet.
            var regular = new[] { new TonWalletMessage(info.NftAddress, info.GramAmount, NftSendMode, info.Payload ?? Array.Empty<byte>(), null) };

            var wallet = clientService.Session.Resolve<IWalletService>();
            var name = gift.ToName();
            var self = ownerId.IsUser(clientService.Options.MyId);
            var recipient = clientService.GetTitle(ownerId);

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                self ? Strings.Gift2MoveToMyGifts : Strings.Gift2TransferShort,
                self ? Strings.Gift2MoveToMyGiftsText : string.Format(Strings.Gift2TransferText, name, recipient),
                self ? Strings.Gift2MoveToMyGifts : Strings.Gift2TransferDo,
                SendAsync);

            AddAttributes(popup, gift);

            _ = EstimateFeeAsync(wallet, popup, regular);

            await navigation.ShowPopupAsync(popup);

            return sent;

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, null, Strings.WalletNotEnoughGramToTransferGift, null);
                if (result == null)
                {
                    return false;
                }

                sent = true;

                _ = ReportAsync(xamlRoot, result.Outcome,
                    string.Format("**{0}**\n{1}", Strings.Gift2TransferredTitle, string.Format(Strings.Gift2TransferredText, name, recipient)),
                    string.Format(Strings.GiftOnchainFailed, name),
                    new DelayedFileSource(clientService, gift.Model.Sticker));

                return true;
            }
        }

        #endregion

        #region Collectibles

        /// <summary>
        /// Transfers an NFT that is not a gift, to an address or .ton name the user types. True once
        /// it is sent.
        /// </summary>
        public static async Task<bool> TransferCollectibleAsync(IClientService clientService, INavigationService navigation, TonNft collectible)
        {
            var wallet = clientService.Session.Resolve<IWalletService>();

            var recipient = await WalletHelper.AskRecipientAsync(navigation, wallet, Strings.WalletTransferCollectible);
            if (recipient == null)
            {
                return false;
            }

            var xamlRoot = navigation.XamlRoot;

            // Emulated before anything is shown: one the wallet cannot send, because it is on sale,
            // not held directly or failing on the item, is never offered for confirmation.
            var transfer = await wallet.PreviewNftTransferAsync(collectible.Address, recipient.Address);
            if (transfer == null)
            {
                await navigation.ShowPopupAsync(Strings.WalletCollectibleTransferPrepareFailed, Strings.WalletTransferCollectible, Strings.OK);
                return false;
            }

            var name = string.IsNullOrEmpty(collectible.Name)
                ? Strings.WalletCollectible
                : collectible.Name;

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                Strings.WalletTransferCollectible,
                string.Empty,
                string.Format(Strings.WalletSendAmount, name),
                SendAsync);

            popup.AddRow(Strings.WalletCollectible, name);
            popup.AddRow(Strings.WalletRecipient, string.IsNullOrEmpty(recipient.Domain)
                ? WalletHelper.DisplayAddress(recipient.Address)
                : recipient.Domain);
            popup.SetFee((long)transfer.FeeNanograms);

            await navigation.ShowPopupAsync(popup);

            return sent;

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, (service, lease) => service.SendNftTransferAsync(transfer, lease), null, null);
                if (result == null)
                {
                    return false;
                }

                sent = true;

                _ = ReportFailureAsync(xamlRoot, result.Outcome, Strings.WalletTransferFailed, null);
                return true;
            }
        }

        #endregion

        #region Selling

        /// <summary>
        /// Puts a gift on sale on-chain, or changes the price of a fixed-price sale when
        /// <paramref name="change"/>. True once the transaction is sent.
        /// </summary>
        public static async Task<bool> ListAsync(IClientService clientService, INavigationService navigation, UpgradedGift gift, long price, bool change)
        {
            var xamlRoot = navigation.XamlRoot;

            // An unlisted gift has no sale yet, so its errors are worded for neither kind.
            bool? isFixed = change ? true : gift.ResaleParameters?.IsFixedOnChain;

            var response = await clientService.SendAsync(new GetGiftOnChainListingTransaction(gift.Name, price));
            if (response is not GiftOnChainSaleTransaction transaction || !TryGetMessages(transaction, out var regular, out var gasless))
            {
                ShowError(clientService, navigation, xamlRoot, response as Error, isFixed);
                return false;
            }

            var wallet = clientService.Session.Resolve<IWalletService>();

            var name = gift.ToName();
            var amount = GiftHelper.FormatGrams(price);
            var total = Total(regular);

            // The server picks the kind of sale, so the wording follows what it sent back.
            var fixedPrice = transaction is GiftOnChainSaleTransactionFixedPrice;

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            if (change)
            {
                popup = new GiftOnChainConfirmPopup(Strings.Gift2ChangePrice, string.Format(Strings.GiftOnchainChangePriceConfirmText, name, amount), Strings.Gift2ChangePrice, SendAsync);
            }
            else
            {
                popup = new GiftOnChainConfirmPopup(
                    fixedPrice ? Strings.GiftOnchainSellFixed : Strings.GiftOnchainSellTitle,
                    string.Format(fixedPrice ? Strings.GiftOnchainSellFixedConfirmText : Strings.GiftOnchainSellConfirmText, name, amount),
                    Strings.GiftOnchainSellButton,
                    SendAsync);
            }

            popup.AddRow(Strings.GiftOnchainRowPrice, FormatPrice(clientService, price));
            AddFeeRows(popup, price, gift.FeeBasisPoints);
            popup.AddRow(Strings.GiftOnchainRowContractFee, GiftHelper.FormatGrams(total));

            if (!change)
            {
                AddAttributes(popup, gift);
                popup.SetNote(fixedPrice ? Strings.GiftOnchainSellNoteFixed : Strings.GiftOnchainSellHint);
            }

            _ = EstimateFeeAsync(wallet, popup, regular);

            await navigation.ShowPopupAsync(popup);

            return sent;

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, gasless, Strings.GiftOnchainNotEnoughForFee, isFixed);
                if (result == null)
                {
                    return false;
                }

                sent = true;

                var sticker = new DelayedFileSource(clientService, gift.Model.Sticker);

                if (change)
                {
                    ToastPopup.Show(xamlRoot, string.Format(Strings.GiftOnchainChangePriceSent, name), sticker);
                    _ = ReportAsync(xamlRoot, result.Outcome, string.Format(Strings.GiftOnchainPriceChanged, name, amount), string.Format(Strings.GiftOnchainFailed, name), sticker);
                }
                else
                {
                    ToastPopup.Show(xamlRoot, string.Format(fixedPrice ? Strings.GiftOnchainSellSentFixed : Strings.GiftOnchainSellSent, name), sticker);
                    _ = ReportAsync(xamlRoot, result.Outcome, string.Format(fixedPrice ? Strings.GiftOnchainListedFixed : Strings.GiftOnchainListed, name), string.Format(Strings.GiftOnchainFailed, name), sticker);
                }

                return true;
            }
        }

        /// <summary>
        /// Takes a gift off its on-chain sale. True once the transaction is sent.
        /// </summary>
        public static async Task<bool> DelistAsync(IClientService clientService, INavigationService navigation, UpgradedGift gift)
        {
            var xamlRoot = navigation.XamlRoot;
            var isFixed = gift.ResaleParameters?.IsFixedOnChain is true;

            var response = await clientService.SendAsync(new GetGiftOnChainDelistingTransaction(gift.Name));
            if (response is not GiftOnChainSaleTransaction transaction || !TryGetMessages(transaction, out var regular, out var gasless))
            {
                ShowError(clientService, navigation, xamlRoot, response as Error, isFixed);
                return false;
            }

            var wallet = clientService.Session.Resolve<IWalletService>();
            var name = gift.ToName();

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                string.Format(Strings.Gift2UnlistTitle, name),
                isFixed ? Strings.GiftOnchainUnlistConfirmTextFixed : Strings.GiftOnchainUnlistConfirmText,
                Strings.Gift2ActionUnlist,
                SendAsync);

            popup.AddRow(Strings.GiftOnchainRowContractFee, GiftHelper.FormatGrams(Total(regular)));

            _ = EstimateFeeAsync(wallet, popup, regular);

            await navigation.ShowPopupAsync(popup);

            return sent;

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, gasless, Strings.GiftOnchainNotEnoughForFee, isFixed);
                if (result == null)
                {
                    return false;
                }

                sent = true;

                var sticker = new DelayedFileSource(clientService, gift.Model.Sticker);

                ToastPopup.Show(xamlRoot, string.Format(Strings.GiftOnchainUnlistSent, name), sticker);
                _ = ReportAsync(xamlRoot, result.Outcome, string.Format(Strings.GiftOnchainUnlisted, name), string.Format(Strings.GiftOnchainFailed, name), sticker);

                return true;
            }
        }

        /// <summary>
        /// A Fragment sale has no price to change: it is cancelled and started again. Offers the
        /// first half of that.
        /// </summary>
        public static async Task<bool> ExplainChangePriceAsync(IClientService clientService, INavigationService navigation, UpgradedGift gift)
        {
            var confirm = await navigation.ShowPopupAsync(Strings.GiftOnchainChangePriceText, Strings.Gift2ChangePrice, Strings.Gift2ActionUnlist, Strings.Cancel);
            if (confirm != ModalPopupResult.Primary)
            {
                return false;
            }

            return await DelistAsync(clientService, navigation, gift);
        }

        #endregion

        #region Offers

        /// <summary>
        /// Whether the current user can offer to buy <paramref name="gift"/> on-chain: it is in a
        /// wallet that is not theirs, takes offers, and is not on sale.
        /// </summary>
        public static bool CanSendOffer(IClientService clientService, UpgradedGift gift)
        {
            var myId = clientService.Options.MyId;

            if (gift.MinOnChainPurchaseOfferGramAmount <= 0
                || gift.ResaleParameters != null
                || !clientService.Options.CanUseTonWallet
                || string.IsNullOrEmpty(gift.GiftAddress)
                || string.IsNullOrEmpty(gift.OwnerAddress)
                || gift.OwnerId.IsUser(myId)
                || gift.HostId.IsUser(myId))
            {
                return false;
            }

            var wallet = clientService.Session.Resolve<IWalletService>();
            return !WalletService.IsSameAddress(gift.OwnerAddress, wallet.State.Address);
        }

        /// <summary>
        /// Offers to buy a gift on-chain. The offered Grams leave the wallet with the transaction and
        /// are held by the offer contract until it is accepted, declined or expires. True once the
        /// transaction is sent.
        /// </summary>
        public static async Task<bool> SendOfferAsync(IClientService clientService, INavigationService navigation, UpgradedGift gift, string ownerName)
        {
            var xamlRoot = navigation.XamlRoot;

            var wallet = clientService.Session.Resolve<IWalletService>();
            var state = await wallet.RestoreAsync();

            if (!state.HasWallet)
            {
                ShowWalletUnavailable(navigation, xamlRoot, true);
                return false;
            }

            var offer = new GiftOfferPopup(clientService, navigation, gift, ownerName, (long)state.BalanceNanograms);

            var confirm = await navigation.ShowPopupAsync(offer);
            if (confirm != ModalPopupResult.Primary)
            {
                return false;
            }

            var amount = offer.GramAmount;

            var response = await clientService.SendAsync(new SendGiftOnChainPurchaseOffer(gift.Name, amount, offer.Duration));
            if (response is not GiftOnChainPurchaseOfferTransaction transaction || transaction.Message == null)
            {
                ShowError(clientService, navigation, xamlRoot, response as Error, null);
                return false;
            }

            // The relayed form is not used for an offer: the offered Grams are part of the message.
            var regular = new[] { transaction.Message };
            var value = transaction.Message.GramAmount;

            var name = gift.ToName();
            var basisPoints = gift.FeeBasisPoints;
            var fee = basisPoints > 0 ? amount / 10000 * basisPoints + amount % 10000 * basisPoints / 10000 : 0;

            // Rounded up to a ten-thousandth of a Gram, the last digit the amounts are written with.
            var payTotal = (value + 99_999) / 100_000 * 100_000;

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                Strings.GiftOnchainOfferConfirmTitle,
                gift.OwnerId != null && !string.IsNullOrEmpty(ownerName)
                    ? string.Format(Strings.GiftOnchainOfferConfirmTextUser, ownerName, name)
                    : string.Format(Strings.GiftOnchainOfferConfirmText, name),
                string.Format(Strings.GiftOnchainOfferButton, GiftHelper.FormatGramValue(amount)),
                SendAsync);

            popup.AddRow(Strings.GiftOnchainOfferRowYourOffer, GiftHelper.FormatGrams(amount - fee));

            if (fee > 0)
            {
                popup.AddRow(Strings.GiftOnchainOfferRowPlatformFee, FormatFee(fee, basisPoints));
            }

            if (value > amount)
            {
                popup.AddRow(Strings.GiftOnchainRowContractFee, GiftHelper.FormatGrams(Math.Max(payTotal - amount, 0)));
            }

            popup.AddRow(Strings.GiftOnchainOfferRowYouPay, GiftHelper.FormatGrams(payTotal));
            popup.AddRow(Strings.GiftOnchainRowYouReceive, name);

            if (transaction.ExpirationDate > 0)
            {
                popup.AddRow(Strings.GiftOnchainOfferRowExpires, Formatter.DateAt(transaction.ExpirationDate));
            }

            popup.SetNote(Strings.GiftOnchainOfferRefundNote);

            _ = EstimateFeeAsync(wallet, popup, regular);

            await navigation.ShowPopupAsync(popup);

            return sent;

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, null, Strings.GiftOnchainNotEnoughToBuy, null);
                if (result == null)
                {
                    return false;
                }

                sent = true;

                var text = string.Format("**{0}**\n{1}", Strings.GiftOnchainOfferSentTitle, string.Format(Strings.GiftOnchainOfferSentText, GiftHelper.FormatGrams(value), Formatter.DateAt(transaction.ExpirationDate)));

                ToastPopup.Show(xamlRoot, text, ToastPopupIcon.Forward);
                _ = ReportFailureAsync(xamlRoot, result.Outcome, string.Format(Strings.GiftOnchainOfferFailed, name), null);

                return true;
            }
        }

        /// <summary>
        /// True while an offer's buttons ignore taps.
        /// </summary>
        public static bool IsOfferBusy(IClientService clientService, long messageId)
        {
            var key = OfferKey(clientService, messageId);

            lock (_busy)
            {
                var now = DateTime.UtcNow;
                return IsHeld(_busy, key, now) || IsHeld(_accepted, key, now) || IsHeld(_declining, key, now);
            }
        }

        /// <summary>
        /// Withdraws the current user's offer, after saying what it will cost.
        /// </summary>
        public static async void WithdrawOffer(IClientService clientService, INavigationService navigation, long messageId, MessageUpgradedGiftOnChainPurchaseOffer offer)
        {
            var xamlRoot = navigation.XamlRoot;
            var key = OfferKey(clientService, messageId);

            if (!TryBegin(key))
            {
                return;
            }

            var response = await clientService.SendAsync(new CancelGiftOnChainPurchaseOffer(messageId));
            if (response is not GiftOnChainPurchaseOfferTransaction transaction || transaction.Message == null)
            {
                Release(_busy, key);
                ShowError(clientService, navigation, xamlRoot, response as Error, null);
                return;
            }

            var wallet = clientService.Session.Resolve<IWalletService>();
            var regular = new[] { transaction.Message };

            // The fee goes into the balance check, so the popup waits for it, though not for long.
            var estimate = wallet.EstimateTransactionFeeAsync(regular);
            var fee = await Task.WhenAny(estimate, Task.Delay(FeeEstimateTimeout)) == estimate && estimate.Result is { } estimated
                ? (long)estimated
                : 0;

            var fine = Math.Max(transaction.FineAmount, 0);
            var refund = Math.Max(offer.GramAmount - fine, 0);

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                Strings.GiftOnchainOfferWithdrawTitle,
                fine > 0
                    ? string.Format(Strings.GiftOnchainOfferWithdrawFee, GiftHelper.FormatGrams(fine), GiftHelper.FormatGrams(refund))
                    : string.Format(Strings.GiftOnchainOfferWithdrawFree, GiftHelper.FormatGrams(refund)),
                fine > 0 ? string.Format(Strings.GiftOnchainOfferWithdrawFor, GiftHelper.FormatGrams(fine)) : Strings.GiftOnchainOfferWithdraw,
                SendAsync);

            Hold(_busy, key, TimeSpan.MaxValue);

            await navigation.ShowPopupAsync(popup);

            if (!sent)
            {
                Release(_busy, key);
            }

            async Task<bool> SendAsync()
            {
                var required = transaction.Message.GramAmount + fee;

                var state = await wallet.RestoreAsync();
                if (state.HasWallet && state.BalanceNanograms < required)
                {
                    await ShowNotEnoughAsync(navigation, popup.XamlRoot, Strings.GiftOnchainNotEnoughForFee, required);
                    return false;
                }

                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, null, Strings.GiftOnchainNotEnoughForFee, null);
                if (result == null)
                {
                    return false;
                }

                sent = true;
                Hold(_busy, key, SentHold);

                ToastPopup.Show(xamlRoot, Strings.GiftOnchainOfferWithdrawSent, ToastPopupIcon.Info);
                _ = ReportFailureAsync(xamlRoot, result.Outcome, string.Format(Strings.GiftOnchainOfferWithdrawFailed, offer.Gift.ToName()), () => Release(_busy, key));

                return true;
            }
        }

        /// <summary>
        /// Opens the sale an offer the current user received would make, from which it can be
        /// accepted or declined.
        /// </summary>
        public static async void ReviewOffer(IClientService clientService, INavigationService navigation, long messageId, MessageUpgradedGiftOnChainPurchaseOffer offer)
        {
            var xamlRoot = navigation.XamlRoot;
            var key = OfferKey(clientService, messageId);

            lock (_busy)
            {
                if (IsHeld(_accepted, key, DateTime.UtcNow))
                {
                    ToastPopup.Show(xamlRoot, Strings.GiftOnchainOfferAcceptSent, ToastPopupIcon.Info);
                    return;
                }
            }

            if (!TryBegin(key))
            {
                return;
            }

            var response = await clientService.SendAsync(new AcceptGiftOnChainPurchaseOffer(messageId));
            if (response is not GiftOnChainPurchaseOfferTransaction transaction || transaction.Message == null)
            {
                Release(_busy, key);

                if (response is Error { Message: "STARGIFT_ONCHAIN_SALE_ACTIVE" })
                {
                    ShowDialog(xamlRoot, Strings.GiftOnchainOfferUnlistFirst, null);
                }
                else
                {
                    ShowError(clientService, navigation, xamlRoot, response as Error, null);
                }

                return;
            }

            var wallet = clientService.Session.Resolve<IWalletService>();
            var regular = new[] { transaction.Message };
            var gasless = transaction.GaslessMessage != null ? new[] { transaction.GaslessMessage } : null;

            var name = offer.Gift.ToName();

            GiftOnChainConfirmPopup popup = null;
            var sent = false;

            popup = new GiftOnChainConfirmPopup(
                Strings.GiftOnchainOfferSellTitle,
                string.Format(Strings.GiftOnchainOfferSellText, name, GiftHelper.FormatGrams(offer.GramAmount)),
                Strings.GiftOnchainOfferSellButton,
                SendAsync);

            popup.SecondaryButtonText = Strings.GiftOnchainOfferDecline;
            popup.IsDismissButtonVisible = true;

            popup.AddRow(Strings.GiftOnchainRowPrice, FormatPrice(clientService, offer.GramAmount));
            AddFeeRows(popup, offer.GramAmount, offer.FeeBasisPoints);

            // A little before the offer's own expiration, so that nothing is signed for an offer
            // that will be gone by the time the transaction lands.
            popup.CloseAt(offer.ExpirationDate - 10);

            _ = EstimateFeeAsync(wallet, popup, regular);

            Hold(_busy, key, TimeSpan.MaxValue);

            var confirm = await navigation.ShowPopupAsync(popup);

            if (sent)
            {
                return;
            }

            Release(_busy, key);

            if (popup.IsExpired)
            {
                ToastPopup.Show(xamlRoot, Strings.GiftOnchainOfferStatusExpired, ToastPopupIcon.Info);
            }
            else if (confirm == ModalPopupResult.Secondary)
            {
                DeclineOffer(clientService, navigation, messageId, offer);
            }

            async Task<bool> SendAsync()
            {
                var result = await GiftOnChainHelper.SendAsync(clientService, navigation, popup.XamlRoot, regular, gasless, Strings.GiftOnchainNotEnoughForFee, null);
                if (result == null)
                {
                    return false;
                }

                sent = true;
                Hold(_busy, key, SentHold);
                Hold(_accepted, key, AcceptHold);

                var sticker = new DelayedFileSource(clientService, offer.Gift.Model.Sticker);

                ToastPopup.Show(xamlRoot, string.Format(Strings.GiftOnchainOfferSellSent, name), sticker);
                _ = ReportAsync(xamlRoot, result.Outcome, string.Format(Strings.GiftOnchainOfferAcceptedToast, name), null, sticker, OnFailed);

                return true;
            }

            void OnFailed()
            {
                Release(_busy, key);
                Release(_accepted, key);

                ToastPopup.Show(xamlRoot, string.Format(Strings.GiftOnchainOfferSellFailed, name), ToastPopupIcon.Error);
            }
        }

        /// <summary>
        /// Declines an offer the current user received. No transaction: the server cancels the
        /// offer contract and the buyer is refunded.
        /// </summary>
        public static async void DeclineOffer(IClientService clientService, INavigationService navigation, long messageId, MessageUpgradedGiftOnChainPurchaseOffer offer)
        {
            var xamlRoot = navigation.XamlRoot;
            var key = OfferKey(clientService, messageId);

            lock (_busy)
            {
                var now = DateTime.UtcNow;

                if (IsHeld(_accepted, key, now))
                {
                    ToastPopup.Show(xamlRoot, Strings.GiftOnchainOfferAcceptSent, ToastPopupIcon.Info);
                    return;
                }
                else if (IsHeld(_declining, key, now))
                {
                    return;
                }
            }

            var text = string.Format(Strings.GiftOnchainOfferDeclineText, GiftHelper.FormatGrams(offer.GramAmount), GiftHelper.NoBreak(offer.Gift.ToName()));

            var confirm = await navigation.ShowPopupAsync(ClientEx.ParseMarkdown(text), Strings.GiftOnchainOfferDeclineTitle, Strings.GiftOnchainOfferDecline, Strings.Cancel, destructive: true);
            if (confirm != ModalPopupResult.Primary)
            {
                return;
            }

            Hold(_declining, key, RequestDebounce);

            var response = await clientService.SendAsync(new DeclineGiftOnChainPurchaseOffer(messageId));
            if (response is Ok)
            {
                Hold(_declining, key, SentHold);
                ToastPopup.Show(xamlRoot, Strings.GiftOnchainOfferDeclined, ToastPopupIcon.Success);
            }
            else
            {
                Release(_declining, key);
                ShowError(clientService, navigation, xamlRoot, response as Error, null);
            }
        }

        private static string OfferKey(IClientService clientService, long messageId)
        {
            return clientService.SessionId + ":" + messageId;
        }

        // One request at a time per offer, and none again for a while after it, as the answer can
        // take that long to change the message.
        private static bool TryBegin(string key)
        {
            lock (_busy)
            {
                var now = DateTime.UtcNow;
                if (IsHeld(_busy, key, now))
                {
                    return false;
                }

                _busy[key] = now + RequestDebounce;
                return true;
            }
        }

        private static bool IsHeld(Dictionary<string, DateTime> map, string key, DateTime now)
        {
            return map.TryGetValue(key, out var until) && until > now;
        }

        private static void Hold(Dictionary<string, DateTime> map, string key, TimeSpan duration)
        {
            lock (_busy)
            {
                map[key] = duration == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow + duration;
            }
        }

        private static void Release(Dictionary<string, DateTime> map, string key)
        {
            lock (_busy)
            {
                map.Remove(key);
            }
        }

        #endregion

        #region Sending

        /// <summary>
        /// Signs and sends, saying why when it doesn't. Null unless the transaction was sent.
        /// </summary>
        private static Task<WalletTransactionResult> SendAsync(IClientService clientService, INavigationService navigation, XamlRoot xamlRoot, IReadOnlyList<TonWalletMessage> regular, IReadOnlyList<TonWalletMessage> gasless, string notEnough, bool? isFixed)
        {
            return SendAsync(clientService, navigation, xamlRoot, (wallet, lease) => wallet.SendTransactionAsync(regular, gasless, lease), notEnough, isFixed);
        }

        /// <param name="notEnough">What a short balance is said with, formatted with the amount
        /// needed; null for the title alone.</param>
        private static async Task<WalletTransactionResult> SendAsync(IClientService clientService, INavigationService navigation, XamlRoot xamlRoot, Func<IWalletService, WalletVault.WalletVaultLease, Task<WalletTransactionResult>> send, string notEnough, bool? isFixed)
        {
            var wallet = clientService.Session.Resolve<IWalletService>();

            using var lease = wallet.CreateLease(navigation);

            if (!await WalletHelper.EnsureBoundAsync(clientService, wallet, navigation, lease))
            {
                return null;
            }

            WalletTransactionResult result;

            try
            {
                result = await send(wallet, lease);
            }
            catch (WalletTransferInProgressException)
            {
                await MessagePopup.ShowNestedAsync(xamlRoot, Strings.WalletTransferInProgress, Strings.AppName, Strings.OK);
                return null;
            }
            catch (WalletAccessDeniedException)
            {
                // Asked to authorize the spend and declined: an answer, not a failure.
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("on-chain gift transaction failed: " + ex.Message);

                ToastPopup.Show(xamlRoot, Strings.GiftOnchainErrorTransactionFailed, ToastPopupIcon.Error);
                return null;
            }

            switch (result.Status)
            {
                case WalletTransactionStatus.Unsupported:
                    ShowDialog(xamlRoot, Strings.GiftOnchainErrorUnsupported, isFixed);
                    return null;
                case WalletTransactionStatus.InsufficientBalance:
                    await ShowNotEnoughAsync(navigation, xamlRoot, notEnough, (long)result.RequiredNanograms);
                    return null;
                case WalletTransactionStatus.Refused:
                    ShowError(clientService, navigation, xamlRoot, result.Error, isFixed);
                    return null;
            }

            return result;
        }

        private static bool TryGetMessages(GiftOnChainSaleTransaction transaction, out IReadOnlyList<TonWalletMessage> regular, out IReadOnlyList<TonWalletMessage> gasless)
        {
            switch (transaction)
            {
                case GiftOnChainSaleTransactionFragment fragment when !string.IsNullOrEmpty(fragment.NftAddress):
                    // One message to the item, carrying the price and the payload. Sent as a regular
                    // transfer whatever is_gasless_transfer_available says: a relayed form would be
                    // a message of its own, and TDLib provides none.
                    regular = new[] { new TonWalletMessage(fragment.NftAddress, fragment.GramAmount, NftSendMode, fragment.Payload ?? Array.Empty<byte>(), null) };
                    gasless = null;
                    return true;
                case GiftOnChainSaleTransactionFixedPrice fixedPrice when fixedPrice.Messages.Count > 0:
                    regular = fixedPrice.Messages;
                    gasless = fixedPrice.GaslessMessages;
                    return true;
            }

            regular = null;
            gasless = null;
            return false;
        }

        private static long Total(IReadOnlyList<TonWalletMessage> messages)
        {
            long total = 0;

            foreach (var message in messages)
            {
                total += message.GramAmount;
            }

            return total;
        }

        private static async Task EstimateFeeAsync(IWalletService wallet, GiftOnChainConfirmPopup popup, IReadOnlyList<TonWalletMessage> messages)
        {
            var fee = await wallet.EstimateTransactionFeeAsync(messages);
            if (fee is { } estimated)
            {
                popup.SetFee((long)estimated);
            }
        }

        private static async Task ReportAsync(XamlRoot xamlRoot, Task<bool> outcome, string landed, string failed, AnimatedImageSource sticker, Action onFailed = null)
        {
            if (await outcome)
            {
                ToastPopup.Show(xamlRoot, landed, sticker);
            }
            else if (onFailed != null)
            {
                onFailed();
            }
            else
            {
                ToastPopup.Show(xamlRoot, failed, ToastPopupIcon.Error);
            }
        }

        private static async Task ReportFailureAsync(XamlRoot xamlRoot, Task<bool> outcome, string failed, Action onFailed)
        {
            if (!await outcome)
            {
                onFailed?.Invoke();
                ToastPopup.Show(xamlRoot, failed, ToastPopupIcon.Error);
            }
        }

        #endregion

        #region Rows

        private static string FormatPrice(IClientService clientService, long nanograms)
        {
            var usd = GiftHelper.FormatUsd(clientService, nanograms);
            return usd != null
                ? GiftHelper.FormatGrams(nanograms) + " (" + usd + ")"
                : GiftHelper.FormatGrams(nanograms);
        }

        /// <summary>
        /// The platform's share of <paramref name="amount"/>, when there is one, and what is left.
        /// </summary>
        private static void AddFeeRows(GiftOnChainConfirmPopup popup, long amount, int basisPoints)
        {
            if (basisPoints < 0)
            {
                return;
            }

            // Split so that the product cannot overflow for any price that fits in a long.
            var fee = amount / 10000 * basisPoints + amount % 10000 * basisPoints / 10000;

            if (basisPoints > 0)
            {
                popup.AddRow(Strings.GiftOnchainOfferRowPlatformFee, FormatFee(fee, basisPoints));
            }

            popup.AddRow(Strings.GiftOnchainRowYouReceive, GiftHelper.FormatGrams(amount - fee));
        }

        private static string FormatFee(long fee, int basisPoints)
        {
            return string.Format("{0} ({1}%)", GiftHelper.FormatGrams(fee), (basisPoints / 100d).ToString("0.##"));
        }

        private static void AddAttributes(GiftOnChainConfirmPopup popup, UpgradedGift gift)
        {
            popup.AddRow(Strings.Gift2AttributeModel, gift.Model.Name);
            popup.AddRow(Strings.Gift2AttributeSymbol, gift.Symbol.Name);
            popup.AddRow(Strings.Gift2AttributeBackdrop, gift.Backdrop.Name);
        }

        #endregion

        #region Errors

        /// <summary>
        /// Says what an on-chain gift request failed with. <paramref name="isFixed"/> picks the
        /// wording for a fixed-price sale or a Fragment one; null when the request was about neither.
        /// </summary>
        public static void ShowError(IClientService clientService, INavigationService navigation, XamlRoot xamlRoot, Error error, bool? isFixed)
        {
            var wordedFixed = isFixed != false;

            switch (error?.Message)
            {
                case "STARGIFT_ONCHAIN_OFFER_INTENT_CONFLICT":
                    ShowToast(xamlRoot, Strings.GiftOnchainOfferErrorTryAgain);
                    break;
                case "STARGIFT_ONCHAIN_OFFER_ALREADY_ACTIVE":
                    ShowToast(xamlRoot, Strings.GiftOnchainOfferErrorAlreadyActive);
                    break;
                case "STARGIFT_ONCHAIN_OFFER_NOT_ACTIVE":
                    ShowToast(xamlRoot, Strings.GiftOnchainErrorOfferNotActive);
                    break;
                case "STARGIFT_ONCHAIN_ITEM_NOT_FOUND":
                case "STARGIFT_ONCHAIN_NOT_FOR_SALE":
                    ShowToast(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorNotForSaleFixed : Strings.GiftOnchainErrorNotForSale);
                    break;
                case "STARGIFT_ONCHAIN_SALE_ACTIVE":
                    ShowToast(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorSaleActiveFixed : Strings.GiftOnchainErrorSaleActive);
                    break;
                case "STARGIFT_ONCHAIN_SALE_INACTIVE":
                    ShowToast(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorSaleInactiveFixed : Strings.GiftOnchainErrorSaleInactive);
                    break;
                case "STARGIFT_ONCHAIN_PRICE_INVALID":
                    ShowDialog(xamlRoot, PriceInvalid(clientService), isFixed);
                    break;
                case "STARGIFT_ONCHAIN_PRICE_CHANGED":
                    ShowDialog(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorPriceChangedFixed : Strings.GiftOnchainErrorPriceChanged, isFixed);
                    break;
                case "STARGIFT_ONCHAIN_SALE_DISABLED":
                    ShowDialog(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorSaleDisabledFixed : Strings.GiftOnchainErrorSaleDisabled, isFixed);
                    break;
                case "STARGIFT_ONCHAIN_NOT_MINTED":
                    ShowDialog(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorNotMintedFixed : Strings.GiftOnchainErrorNotMinted, isFixed);
                    break;
                case "STARGIFT_ONCHAIN_SELF_PURCHASE":
                    ShowDialog(xamlRoot, wordedFixed ? Strings.GiftOnchainErrorSelfPurchaseFixed : Strings.GiftOnchainErrorSelfPurchase, isFixed);
                    break;
                case "STARGIFT_ONCHAIN_NOT_OWNER":
                    // Fixed only when known to be: "not in your wallet" is the safer reading otherwise.
                    ShowDialog(xamlRoot, isFixed == true ? Strings.GiftOnchainErrorNotOwnerFixed : Strings.GiftOnchainErrorNotOwner, isFixed);
                    break;
                case "PEER_INVALID":
                    ShowDialog(xamlRoot, isFixed == false ? Strings.GiftOnchainErrorBuyForSelfOnly : Strings.GiftOnchainErrorBuyRecipient, isFixed);
                    break;
                case "WALLET_UNAVAILABLE":
                    ShowWalletUnavailable(navigation, xamlRoot, wordedFixed);
                    break;
                case "WALLET_CREATING":
                    ShowToast(xamlRoot, Strings.GiftOnchainErrorWalletCreating);
                    break;
                default:
                    ToastPopup.ShowError(xamlRoot, error ?? new Error(0, "NULL_ERROR"));
                    break;
            }
        }

        private static string PriceInvalid(IClientService clientService)
        {
            var options = clientService.Options;

            // The range is in hundredths of a Gram, and a hundredth is 10^7 nanograms.
            var min = options.GiftResaleGramCentCountMin;
            var max = options.GiftResaleGramCentCountMax;

            return min > 0 && max > min
                ? string.Format(Strings.GiftOnchainErrorPriceInvalidRange, GiftHelper.FormatGrams(min * 10_000_000), GiftHelper.FormatGrams(max * 10_000_000))
                : Strings.GiftOnchainErrorPriceInvalid;
        }

        private static void ShowToast(XamlRoot xamlRoot, string text)
        {
            ToastPopup.Show(xamlRoot, text, ToastPopupIcon.Error);
        }

        private static void ShowDialog(XamlRoot xamlRoot, string text, bool? isFixed)
        {
            var title = isFixed switch
            {
                true => Strings.GiftOnchainErrorTitleFixed,
                false => Strings.GiftOnchainErrorTitle,
                _ => Strings.AppName
            };

            _ = MessagePopup.ShowNestedAsync(xamlRoot, text, title, Strings.OK);
        }

        private static async void ShowWalletUnavailable(INavigationService navigation, XamlRoot xamlRoot, bool isFixed)
        {
            var text = isFixed ? Strings.GiftOnchainErrorWalletUnavailableFixed : Strings.GiftOnchainErrorWalletUnavailable;

            var confirm = await MessagePopup.ShowNestedAsync(xamlRoot, text, Strings.GiftOnchainWalletTitle, Strings.GiftOnchainOpenWallet, Strings.Cancel);
            if (confirm == ModalPopupResult.Primary)
            {
                navigation.NavigateToWallet();
            }
        }

        private static async Task ShowNotEnoughAsync(INavigationService navigation, XamlRoot xamlRoot, string format, long required)
        {
            var message = format != null
                ? string.Format(format, GiftHelper.FormatGrams(required))
                : string.Empty;

            var confirm = await MessagePopup.ShowNestedAsync(xamlRoot, message, Strings.WalletNotEnoughGram, Strings.WalletTopUp, Strings.WalletNotNow);
            if (confirm == ModalPopupResult.Primary)
            {
                navigation.NavigateToWallet();
            }
        }

        #endregion
    }
}
