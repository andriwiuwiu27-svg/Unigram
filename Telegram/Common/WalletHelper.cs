//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.Wallet;
using Telegram.Td.Api;
using Telegram.Views.Popups;
using Telegram.Views.Wallet.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Common
{
    public static class WalletHelper
    {
        private const int DappNameLimit = 64;

        /// <summary>
        /// A dApp's name as it is safe to show: one line, no bidi controls, at most
        /// <see cref="DappNameLimit"/> characters - or <paramref name="fallback"/> when nothing is
        /// left.
        /// </summary>
        /// <remarks>
        /// The name is whatever the dApp wrote. A bidi override could turn the sentence it sits in
        /// around, and a long or multi-line one push the domain - the part that can be judged -
        /// off the screen.
        /// </remarks>
        public static string DappName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            var builder = new StringBuilder(Math.Min(name.Length, DappNameLimit));

            foreach (var character in name)
            {
                if (builder.Length >= DappNameLimit)
                {
                    break;
                }

                if (character is >= '\u200E' and <= '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069')
                {
                    continue;
                }

                builder.Append(char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character);
            }

            if (builder.Length > 0 && char.IsHighSurrogate(builder[builder.Length - 1]))
            {
                builder.Length--;
            }

            var result = builder.ToString().Trim();
            return result.Length > 0 ? result : fallback;
        }

        public static bool IsValidAddress(string address)
        {
            try
            {
                return !string.IsNullOrEmpty(address) && WalletEngine.WalletEngineMethods.IsValidTonAddress(address);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Somewhere to send to, typed: an address, or a .ton name resolved to one. Null when the
        /// user cancels or what they typed leads nowhere, which has been said by then.
        /// </summary>
        public static async Task<WalletRecipient> AskRecipientAsync(INavigationService navigation, IWalletService wallet, string title)
        {
            var popup = new InputPopup
            {
                Title = title,
                PlaceholderText = Strings.WalletAddressOrName,
                PrimaryButtonText = Strings.Next,
                SecondaryButtonText = Strings.Cancel,
                MinLength = 1
            };

            var confirm = await navigation.ShowPopupAsync(popup);
            if (confirm != ModalPopupResult.Primary)
            {
                return null;
            }

            var address = popup.Text.Trim();
            var domain = string.Empty;

            if (address.EndsWith(".ton", StringComparison.OrdinalIgnoreCase))
            {
                domain = address;

                try
                {
                    address = await wallet.ResolveDnsAsync(address);
                }
                catch (Exception ex)
                {
                    // An unknown name is one of the ways this fails, so it is said the same way.
                    Logger.Error("wallet name could not be resolved: " + ex.Message);
                    address = null;
                }

                if (string.IsNullOrEmpty(address))
                {
                    navigation.ShowToast(string.Format(Strings.WalletSearchNoResults, domain), ToastPopupIcon.Error);
                    return null;
                }
            }
            else if (!IsValidAddress(address))
            {
                navigation.ShowToast(Strings.WalletInvalidAddress, ToastPopupIcon.Error);
                return null;
            }

            return new WalletRecipient(address, domain);
        }

        /// <summary>
        /// An address in the 48-character form the send card has room for: a raw one is written
        /// as the bounceable friendly address it stands for, anything else is returned as it is.
        /// </summary>
        /// <remarks>
        /// The raw form is 66 characters, and cut to fit it would show the user an address that is
        /// not the one being paid. Bounceable, because that is how a raw address is sent.
        /// </remarks>
        public static string DisplayAddress(string address)
        {
            try
            {
                // Friendly already, which is nearly always: the parse is a call into the engine,
                // and a chat bubble asks on every render.
                if (string.IsNullOrEmpty(address) || address.Length == 48 || WalletEngine.WalletEngineMethods.ParseTonAddress(address) is not { Format: WalletEngine.TonAddressFormat.Raw } info)
                {
                    return address;
                }

                var colon = info.Raw.IndexOf(':');
                var hash = info.Raw.Substring(colon + 1);

                var bytes = new byte[36];
                bytes[0] = 0x11;
                bytes[1] = unchecked((byte)info.Workchain);

                for (int i = 0; i < 32; i++)
                {
                    bytes[2 + i] = Convert.ToByte(hash.Substring(i * 2, 2), 16);
                }

                var crc = Crc16(bytes, 34);
                bytes[34] = (byte)(crc >> 8);
                bytes[35] = (byte)(crc & 0xFF);

                return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            }
            catch
            {
                return address;
            }
        }

        // CRC-16/XMODEM, which is what a friendly TON address ends with.
        private static ushort Crc16(byte[] data, int length)
        {
            int crc = 0;

            for (int i = 0; i < length; i++)
            {
                crc ^= data[i] << 8;

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = ((crc & 0x8000) != 0
                        ? (crc << 1) ^ 0x1021
                        : crc << 1) & 0xFFFF;
                }
            }

            return (ushort)crc;
        }

        /// <summary>
        /// The host a TON Connect URL names, in the form a domain is compared and signed in -
        /// punycode, lower case, no trailing dot - or null when it names none.
        /// </summary>
        /// <remarks>
        /// Punycode so that a look-alike in another script cannot pass for the name it imitates,
        /// either on screen or in a comparison.
        /// </remarks>
        public static string TonConnectHost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return null;
            }

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            return host.Length > 0 ? host : null;
        }

        public static bool TryToCurrency(IClientService clientService, WalletState state, BigInteger nanograms, out BigInteger units, out string currency)
        {
            units = BigInteger.Zero;

            if (!TryGetRate(clientService, state, out var rate))
            {
                if (TryToUsd(clientService, nanograms, out units))
                {
                    currency = "USD";
                    return true;
                }

                currency = null;
                return false;
            }

            // nanograms * usd / 1e15 gives dollars; the rest carries it into the chosen currency
            // and into CurrencyDecimals. One division, at the end, so nothing is truncated twice.
            units = nanograms * rate.Usd * rate.Scaled * CurrencyScale
                / (NanogramsPerMillionGram * RateScale);

            currency = state.Currency;
            return true;
        }

        /// <summary>
        /// What an amount of grams is worth in the currency the user chose.
        /// </summary>
        /// <remarks>
        /// Two steps, both of them the account's: grams to dollars at <c>million_gram_to_usd_rate</c>,
        /// dollars to the chosen currency at the rate TDLib quotes for it. In floating point, because
        /// the amounts here are routinely smaller than a cent and integer division lands on whole ones.
        ///
        /// The rate is how many of the currency one dollar buys, so dollars are multiplied by it.
        /// Dividing gets an answer that looks plausible and is wrong by the square of the rate -
        /// 1.19 grams read as 0.44 AED where it is 5.98.
        /// </remarks>
        /// <summary>
        /// An amount of grams in the wallet's currency, or false when it cannot be said.
        /// </summary>
        /// <remarks>
        /// **Answering false is the point of this returning a bool.** Both rates arrive from the
        /// account and neither is there at once, and the obvious fallback - leave the amount in
        /// dollars - is the one thing that must not happen: it produces a number that is wrong by
        /// the rate and labels it with the currency the user chose. There is no reading of the
        /// screen that recovers from that, so the caller is made to decide what to show instead.
        /// </remarks>
        public static bool TryToCurrency(IClientService clientService, WalletState state, BigInteger nanograms, out BigInteger units)
        {
            units = BigInteger.Zero;

            if (!TryGetRate(clientService, state, out var rate))
            {
                return false;
            }

            // nanograms * usd / 1e15 gives dollars; the rest carries it into the chosen currency
            // and into CurrencyDecimals. One division, at the end, so nothing is truncated twice.
            units = nanograms * rate.Usd * rate.Scaled * CurrencyScale
                / (NanogramsPerMillionGram * RateScale);

            return true;
        }

        /// <summary>
        /// The first of the two steps on its own, in <see cref="CurrencyDecimals"/>, for a caller
        /// that wants dollars and so needs no currency rate to be in yet.
        /// </summary>
        public static bool TryToUsd(IClientService clientService, BigInteger nanograms, out BigInteger units)
        {
            units = BigInteger.Zero;

            var usd = clientService.Options.MillionGramToUsdRate;
            if (usd <= 0)
            {
                return false;
            }

            units = nanograms * usd * CurrencyScale / NanogramsPerMillionGram;
            return true;
        }

        /// <summary>
        /// How many decimals <see cref="TryToCurrency"/> counts in. Deeper than any currency, so
        /// that a fee worth a fraction of a cent survives to be formatted.
        /// </summary>
        public const int CurrencyDecimals = 9;

        private static readonly BigInteger CurrencyScale = BigInteger.Pow(10, CurrencyDecimals);

        // A million grams is 1e6 * 1e9 nanograms, which is what million_gram_to_usd_rate is priced
        // against.
        private static readonly BigInteger NanogramsPerMillionGram = BigInteger.Pow(10, 15);

        // The currency rate arrives from the account as a double and is the one value here that
        // cannot be exact. It is turned into a fraction over this once, at the edge, so that
        // everything after it is integer arithmetic rather than a chain of rounded multiplications.
        private const int RateDecimals = 9;

        private static readonly BigInteger RateScale = BigInteger.Pow(10, RateDecimals);

        private static bool TryGetRate(IClientService clientService, WalletState state, out (BigInteger Usd, BigInteger Scaled) rate)
        {
            rate = default;

            var usd = clientService.Options.MillionGramToUsdRate;
            if (usd <= 0 || state is not { CurrencyRate: > 0 })
            {
                return false;
            }

            var scaled = new BigInteger(Math.Round(state.CurrencyRate * (double)RateScale));
            if (scaled <= BigInteger.Zero)
            {
                return false;
            }

            rate = (usd, scaled);
            return true;
        }

        /// <summary>
        /// The same two steps backwards, for an amount typed in money rather than in grams.
        /// </summary>
        /// <summary>
        /// The same two steps backwards, for an amount typed in money rather than in grams.
        /// </summary>
        /// <remarks>
        /// This one decides what leaves the wallet, so a missing rate has to stop the transfer
        /// rather than guess at it: treating the typed amount as dollars would send whatever the
        /// rate would have divided out - for a currency at 3.67 to the dollar, nearly four times
        /// what was asked for.
        /// </remarks>
        public static bool TryToNanograms(IClientService clientService, WalletState state, BigInteger units, int exponent, out BigInteger nanograms)
        {
            nanograms = BigInteger.Zero;

            if (!TryGetRate(clientService, state, out var rate))
            {
                return false;
            }

            // The inverse of the above, and truncating for the same reason: what is sent is never
            // more than what was asked for.
            nanograms = units * NanogramsPerMillionGram * RateScale
                / (BigInteger.Pow(10, exponent) * rate.Usd * rate.Scaled);

            return true;
        }

        /// <summary>
        /// Makes sure this device can sign for the account's wallet, asking for whatever that
        /// takes, and returns whether it now can.
        /// </summary>
        /// <remarks>
        /// Binding is not a step the user chooses: it is what the first action that needs the key
        /// does on their behalf. The account password buys the phrase from the cloud backup, the
        /// phrase goes into protected storage, and nothing asks for that password again - the
        /// device has the key from then on, and the prompts after this are its own.
        ///
        /// So every action that signs, reveals or decrypts calls this first, with its own lease,
        /// and gives up quietly if it comes back false: the user has already been told why, or has
        /// said no. A device that is already bound asks nothing and leaves the lease as it was.
        /// </remarks>
        public static async Task<bool> EnsureBoundAsync(IClientService clientService, IWalletService wallet, INavigationService navigation, WalletVault.WalletVaultLease lease)
        {
            var bound = await BindAsync(clientService, wallet, navigation, lease);
            return bound.IsBound;
        }

        /// <summary>
        /// The same, answering with the outcome rather than whether it is bound.
        /// </summary>
        public static async Task<WalletBindOutcome> BindAsync(IClientService clientService, IWalletService wallet, INavigationService navigation, WalletVault.WalletVaultLease lease)
        {
            var state = await wallet.RestoreAsync();
            if (await wallet.RecoverVaultAsync())
            {
                state = wallet.State;
            }

            if (state.CanSign)
            {
                return new WalletBindOutcome(true, null);
            }
            else if (!state.HasWallet)
            {
                // Nothing to bind to. The server creates wallets, so there is also nothing to
                // offer here.
                return new WalletBindOutcome(false, null);
            }

            if (state.CanExportPhrase)
            {
                return await BindFromCloudAsync(wallet, navigation, lease);
            }

            // No cloud copy to read, so the phrase has to come from the user. The import popup
            // binds on its own and refuses a phrase that belongs to another wallet.
            await navigation.ShowPopupAsync(new WalletImportPopup(wallet, navigation, lease));
            return new WalletBindOutcome(wallet.State.CanSign, null);
        }

        private static async Task<WalletBindOutcome> BindFromCloudAsync(IWalletService wallet, INavigationService navigation, WalletVault.WalletVaultLease lease)
        {
            var xamlRoot = navigation.XamlRoot;
            var password = string.Empty;

            while (true)
            {
                var outcome = await BindWithPasswordAsync(wallet, navigation, password, lease);
                if (outcome == BindOutcome.Bound)
                {
                    return new WalletBindOutcome(true, password);
                }
                else if (outcome == BindOutcome.Failed)
                {
                    return new WalletBindOutcome(false, null);
                }

                password = await RequestPasswordAsync(xamlRoot, outcome == BindOutcome.WrongPassword && password.Length > 0);
                if (password == null)
                {
                    return new WalletBindOutcome(false, null);
                }
            }
        }

        /// <summary>
        /// Runs a wallet request that takes the account password, returning false when the user
        /// dismissed the prompt. Every other failure is thrown.
        /// </summary>
        /// <remarks>
        /// TDLib's contract, documented on <c>getTonWalletSecretPhrase</c>: an empty password
        /// first, and the user's only once the server answers <c>PASSWORD_MISSING</c>. The server
        /// decides whether a request needs one, so an account with 2-step verification can still
        /// go through without being asked.
        /// </remarks>
        public static async Task<bool> RunWithPasswordAsync(XamlRoot xamlRoot, Func<string, Task> request)
        {
            var password = string.Empty;

            while (true)
            {
                bool wrong;

                try
                {
                    await request(password);
                    return true;
                }
                catch (WalletRequestException ex) when (ex.IsPasswordMissing || ex.IsInvalidPassword)
                {
                    wrong = ex.IsInvalidPassword && password.Length > 0;
                }

                password = await RequestPasswordAsync(xamlRoot, wrong);
                if (password == null)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// What the user typed, or null when they dismissed the prompt.
        /// </summary>
        private static async Task<string> RequestPasswordAsync(XamlRoot xamlRoot, bool wrong)
        {
            if (wrong)
            {
                await ShowMessageAsync(xamlRoot, Strings.CheckPasswordWrong, Strings.TwoStepVerification);
            }

            // Nested where something modal is already up - the wallet's own screens are popups -
            // which is the test ViewModelBase makes for the same reason.
            var result = ModalPopup.IsAnyPopupOpen(xamlRoot)
                ? await InputPopup.ShowNestedAsync(xamlRoot, InputPopupType.Password, Strings.PleaseEnterCurrentPasswordWithdraw, Strings.TwoStepVerification, Strings.LoginPassword, Strings.OK, Strings.Cancel)
                : await InputPopup.ShowAsync(xamlRoot, InputPopupType.Password, Strings.PleaseEnterCurrentPasswordWithdraw, Strings.TwoStepVerification, Strings.LoginPassword, Strings.OK, Strings.Cancel);

            return result.Result == ModalPopupResult.Primary ? result.Text : null;
        }

        /// <summary>
        /// Whether the device ended up bound, and the account password that got it there.
        /// </summary>
        public readonly struct WalletBindOutcome
        {
            public WalletBindOutcome(bool isBound, string password)
            {
                IsBound = isBound;
                Password = password;
            }

            public bool IsBound { get; }

            /// <summary>
            /// What the user typed, or an empty string where the server did not ask for one. Null
            /// when no password was used - already bound, or never got that far.
            /// </summary>
            public string Password { get; }
        }

        /// <summary>
        /// A top-up comes from a card unless the provider is the crypto one.
        /// </summary>
        public static bool IsCryptoTopUp(TonWalletTransactionTypeOnRampDeposit deposit)
        {
            return string.Equals(deposit.ProviderName, "walt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(deposit.ProviderName, "wallet", StringComparison.OrdinalIgnoreCase);
        }

        public static string CapitalizeProvider(string provider)
        {
            return provider.Length > 0 && char.IsLower(provider[0])
                ? char.ToUpper(provider[0]) + provider.Substring(1)
                : provider;
        }

        /// <summary>
        /// What a gift market transaction did, read once from its type for both the history row
        /// and the transaction popup.
        /// </summary>
        public readonly struct GiftMarketActivity
        {
            private GiftMarketActivity(TonNft nft, string title, string status, string glyph, bool outgoing, long amount, int items, long price, long oldPrice, int expirationDate, long cancellationFee, string counterparty, string counterpartyAddress)
            {
                Nft = nft;
                Title = title;
                Status = status;
                Glyph = glyph;
                IsOutgoing = outgoing;
                Amount = amount;
                Items = items;
                Price = price;
                OldPrice = oldPrice;
                ExpirationDate = expirationDate;
                CancellationFee = cancellationFee;
                Counterparty = counterparty;
                CounterpartyAddress = counterpartyAddress;
            }

            /// <summary>
            /// Null when the gift is not known to the explorer.
            /// </summary>
            public TonNft Nft { get; }

            public string Title { get; }

            public string Status { get; }

            public string Glyph { get; }

            /// <summary>
            /// Drawn on blue rather than green: the gift or the money left the wallet and has not
            /// been paid for yet.
            /// </summary>
            public bool IsOutgoing { get; }

            /// <summary>
            /// Signed nanograms, 0 when the row moves the gift rather than money.
            /// </summary>
            public long Amount { get; }

            /// <summary>
            /// +1 or -1 when the gift itself came or went, 0 otherwise.
            /// </summary>
            public int Items { get; }

            /// <summary>
            /// 0 when the transaction carries no price of its own.
            /// </summary>
            public long Price { get; }

            /// <summary>
            /// Non-zero only for a listing that changed the price, which makes <see cref="Price"/>
            /// the new one.
            /// </summary>
            public long OldPrice { get; }

            public int ExpirationDate { get; }

            public long CancellationFee { get; }

            /// <summary>
            /// The other side's role, null when there is none.
            /// </summary>
            public string Counterparty { get; }

            public string CounterpartyAddress { get; }

            public static bool TryGet(TonWalletTransactionType type, out GiftMarketActivity activity)
            {
                const string Swap = Icons.ArrowSync;
                const string Out = Icons.ArrowCircleUpFilled;
                const string In = Icons.ArrowCircleDownFilled;

                switch (type)
                {
                    case TonWalletTransactionTypeTelegramGiftPurchase purchase:
                        var paid = Math.Abs(purchase.Amount);
                        activity = new GiftMarketActivity(purchase.Nft, string.Format(Strings.WalletOnchainGiftBoughtFor, GiftHelper.FormatGrams(paid)), Strings.WalletOnchainGiftStatusPurchased,
                            Swap, false, 0, 1, paid, 0, 0, 0, Strings.GiftOnchainRowSeller, string.Empty);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftSale sale:
                        activity = new GiftMarketActivity(sale.Nft, string.Format(Strings.WalletOnchainGiftSoldFor, GiftHelper.FormatGrams(Math.Abs(sale.Amount))), Strings.WalletOnchainGiftStatusSold,
                            Swap, false, sale.Amount, 0, 0, 0, 0, 0, Strings.GiftOnchainRowBuyer, string.Empty);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftListing listing when listing.OldPrice > 0:
                        // A price change leaves the gift where it was, so neither it nor any money moves.
                        activity = new GiftMarketActivity(listing.Nft, string.Format(Strings.WalletOnchainGiftRepriced, GiftHelper.FormatGrams(listing.Price)), Strings.WalletOnchainGiftStatusPriceChanged,
                            Icons.TagFilled, false, 0, 0, listing.Price, listing.OldPrice, 0, 0, null, string.Empty);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftListing listing:
                        var listed = listing.Price > 0 ? string.Format(Strings.WalletOnchainGiftPutOnSaleFor, GiftHelper.FormatGrams(listing.Price)) : Strings.WalletOnchainGiftPutOnSale;
                        activity = new GiftMarketActivity(listing.Nft, listed, Strings.WalletOnchainGiftStatusPutOnSale,
                            Out, true, 0, -1, listing.Price, 0, 0, 0, null, string.Empty);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftDelisting delisting:
                        activity = new GiftMarketActivity(delisting.Nft, Strings.WalletOnchainGiftRemovedFromSale, Strings.WalletOnchainGiftStatusRemovedFromSale,
                            In, false, 0, 1, 0, 0, 0, 0, null, string.Empty);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftOfferCreation creation:
                        activity = new GiftMarketActivity(creation.Nft, Strings.WalletOnchainGiftOfferSent, Strings.WalletOnchainGiftOfferSent,
                            Out, true, -Math.Abs(creation.Amount), 0, creation.Price, 0, creation.ExpirationDate, 0, Strings.WalletRecipient, creation.CounterpartyAddress);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftOfferAcceptance acceptance:
                        // The buyer gets the gift and the seller the price.
                        activity = new GiftMarketActivity(acceptance.Nft, string.Format(Strings.WalletOnchainGiftOfferAcceptedFor, GiftHelper.FormatGrams(acceptance.Price)), Strings.WalletOnchainGiftStatusOfferAccepted,
                            Swap, false, acceptance.IsBuyer ? 0 : acceptance.Price, acceptance.IsBuyer ? 1 : 0, acceptance.Price, 0, 0, 0,
                            acceptance.IsBuyer ? Strings.GiftOnchainRowSeller : Strings.GiftOnchainRowBuyer, acceptance.CounterpartyAddress);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftOfferCancellation cancellation:
                        activity = new GiftMarketActivity(cancellation.Nft, Strings.WalletOnchainGiftOfferCancelled, Strings.WalletOnchainGiftOfferCancelled,
                            In, false, cancellation.RefundAmount, 0, cancellation.Price, 0, 0, cancellation.FineAmount, Strings.WalletRecipient, cancellation.CounterpartyAddress);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftOfferDecline decline:
                        activity = new GiftMarketActivity(decline.Nft, Strings.WalletOnchainGiftOfferDeclined, Strings.WalletOnchainGiftOfferDeclined,
                            In, false, decline.RefundAmount, 0, decline.Price, 0, 0, 0, Strings.WalletRecipient, decline.CounterpartyAddress);
                        return true;
                    case TonWalletTransactionTypeTelegramGiftOfferExpiration expiration:
                        activity = new GiftMarketActivity(expiration.Nft, Strings.WalletOnchainGiftOfferExpired, Strings.WalletOnchainGiftOfferExpired,
                            In, false, expiration.RefundAmount, 0, expiration.Price, 0, 0, 0, Strings.WalletRecipient, expiration.CounterpartyAddress);
                        return true;
                    default:
                        activity = default;
                        return false;
                }
            }
        }

        private enum BindOutcome
        {
            Bound,
            PasswordMissing,
            WrongPassword,
            Failed
        }

        /// <summary>
        /// One attempt at the cloud phrase, with the password typed so far - an empty one until
        /// the server asks for it.
        /// </summary>
        private static async Task<BindOutcome> BindWithPasswordAsync(IWalletService wallet, INavigationService navigation, string password, WalletVault.WalletVaultLease lease)
        {
            try
            {
                var bound = await wallet.BindFromCloudAsync(password, lease);
                if (bound.Failure == null)
                {
                    return BindOutcome.Bound;
                }

                // The cloud's own phrase, refused by the engine or belonging to another wallet:
                // the server and the engine disagreeing, which is nothing the user did and
                // nothing they can fix by typing the password again.
                Logger.Error("wallet binding refused: " + bound.Failure);
            }
            catch (WalletRequestException ex) when (ex.IsPasswordMissing)
            {
                return BindOutcome.PasswordMissing;
            }
            catch (WalletRequestException ex) when (ex.IsInvalidPassword)
            {
                return BindOutcome.WrongPassword;
            }
            catch (WalletAccessDeniedException)
            {
                // Storing the phrase asked for the vault and they declined: an answer, not a fault.
                return BindOutcome.Failed;
            }
            catch (Exception ex)
            {
                // What is left is the phrase not arriving at all: the export failed for a reason
                // other than the password.
                Logger.Error("wallet binding failed: " + ex.Message);
            }

            await ShowMessageAsync(navigation.XamlRoot, Strings.WalletSetupFailed, Strings.WalletTitle);
            return BindOutcome.Failed;
        }

        private static Task<ModalPopupResult> ShowMessageAsync(XamlRoot xamlRoot, string message, string title)
        {
            return ModalPopup.IsAnyPopupOpen(xamlRoot)
                ? MessagePopup.ShowNestedAsync(xamlRoot, message, title, Strings.OK)
                : MessagePopup.ShowAsync(xamlRoot, message, title, Strings.OK);
        }
    }

    /// <param name="Domain">The .ton name the address was resolved from, or empty: kept because it
    /// is what the user typed, and what the history should say the transfer went to.</param>
    public sealed record WalletRecipient(string Address, string Domain);
}
