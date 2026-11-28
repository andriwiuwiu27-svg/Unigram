//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Streams;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Windows.UI;
using Windows.UI.Xaml;

namespace Telegram.Controls.Messages.Service
{
    public sealed partial class MessageUpgradedGiftOnChainPurchaseOfferContent : MessageService
    {
        private static readonly Color _ribbonSold = Color.FromArgb(0xFF, 0xF5, 0x59, 0x51);

        // The time left is part of the text, which is rebuilt every second while the offer is open.
        private DispatcherTimer _countdown;

        public MessageUpgradedGiftOnChainPurchaseOfferContent()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        protected override void UpdateContent(MessageViewModel message)
        {
            if (message.Content is not MessageUpgradedGiftOnChainPurchaseOffer offer)
            {
                return;
            }

            Pattern.Update(message.ClientService, offer.Gift);
            Animation.Source = DelayedFileSource.FromSticker(message.ClientService, offer.Gift.Model.Sticker);

            if (!offer.IsBuyer && offer.State is GiftOnChainPurchaseOfferStateAccepted or GiftOnChainPurchaseOfferStateSettled)
            {
                Ribbon.Text = Strings.GiftOnchainOfferRibbonSold;
                RibbonTop.Color = _ribbonSold;
                RibbonBottom.Color = _ribbonSold;
            }
            else
            {
                Ribbon.Text = string.Format("#{0:N0}", offer.Gift.Number);
                RibbonTop.Color = offer.Gift.Backdrop.Colors.CenterColor.ToColor().Darken();
                RibbonBottom.Color = offer.Gift.Backdrop.Colors.EdgeColor.ToColor().Darken();
            }

            // The owner can answer only before the expiration date; the buyer can withdraw for as
            // long as the offer is still pending.
            var now = DateTime.Now.ToUnixTimeSeconds();
            var pending = offer.State is GiftOnChainPurchaseOfferStatePending;

            if (pending && (offer.IsBuyer || now < offer.ExpirationDate))
            {
                ActionButton.Content = offer.IsBuyer ? Strings.GiftOnchainOfferWithdraw : Strings.GiftOnchainOfferReview;
                Action.Visibility = Visibility.Visible;
                Service.CornerRadius = new CornerRadius(11, 11, 4, 4);
            }
            else
            {
                Action.Visibility = Visibility.Collapsed;
                Service.CornerRadius = new CornerRadius(11);
            }

            UpdateCountdown(pending && now <= offer.ExpirationDate);
        }

        public override void Recycle()
        {
            UpdateCountdown(false);
            base.Recycle();
        }

        private void UpdateCountdown(bool running)
        {
            if (running)
            {
                if (_countdown == null)
                {
                    _countdown = new DispatcherTimer();
                    _countdown.Interval = TimeSpan.FromSeconds(1);
                    _countdown.Tick += OnTick;
                }

                _countdown.Start();
            }
            else
            {
                _countdown?.Stop();
            }
        }

        private void OnTick(object sender, object e)
        {
            if (Message is not MessageViewModel message || message.Content is not MessageUpgradedGiftOnChainPurchaseOffer offer)
            {
                UpdateCountdown(false);
                return;
            }

            Text.SetText(message.ClientService, MessageServiceText.GetEntities(message, true));

            // One tick past the expiration date, so that the countdown gives way to "expiring".
            if (DateTime.Now.ToUnixTimeSeconds() > offer.ExpirationDate)
            {
                UpdateContent(message);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Message is MessageViewModel { Content: MessageUpgradedGiftOnChainPurchaseOffer offer })
            {
                UpdateCountdown(offer.State is GiftOnChainPurchaseOfferStatePending && DateTime.Now.ToUnixTimeSeconds() <= offer.ExpirationDate);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UpdateCountdown(false);
        }

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            if (Message is not MessageViewModel message || message.Content is not MessageUpgradedGiftOnChainPurchaseOffer offer)
            {
                return;
            }

            // A request, a sheet or a transaction for this offer is already under way.
            if (GiftOnChainHelper.IsOfferBusy(message.ClientService, message.Id))
            {
                return;
            }

            if (offer.IsBuyer)
            {
                GiftOnChainHelper.WithdrawOffer(message.ClientService, message.Delegate.NavigationService, message.Id, offer);
            }
            else
            {
                GiftOnChainHelper.ReviewOffer(message.ClientService, message.Delegate.NavigationService, message.Id, offer);
            }
        }
    }
}
