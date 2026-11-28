//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Controls.Media;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.ViewModels.Premium;
using Telegram.Views.Premium.Popups;
using Telegram.Views.Settings;
using Telegram.Views.Settings.Popups;
using Telegram.Views.Settings.Privacy;
using Telegram.Views.Stars.Popups;
using Windows.UI.Xaml.Controls;

namespace Telegram.ViewModels
{
    public enum ChatListHintType
    {
        AddLoginPasskey,
        ExtendPremium,
        Custom,
        ExtendStarSubscriptions,
        BirthdaysToday,
        SetBirthdate,
        GiftPremiumForChristmas,
        RestorePremium,
        SubscribeToAnnualPremium,
        SetProfilePhoto
    }

    public sealed class ChatListHint
    {
        public ChatListHint(ChatListHintType type, SuggestedAction action, FormattedText title, FormattedText subtitle, bool canHide, IList<MessageSender> users = null)
        {
            Type = type;
            Action = action;
            Title = title;
            Subtitle = subtitle;
            CanHide = canHide;
            Users = users ?? Array.Empty<MessageSender>();
        }

        public ChatListHintType Type { get; }

        /// <summary>
        /// The suggested action the hint stands for, null for the birthdays of contacts, which aren't one.
        /// </summary>
        public SuggestedAction Action { get; }

        public FormattedText Title { get; }

        public FormattedText Subtitle { get; }

        public bool CanHide { get; }

        public IList<MessageSender> Users { get; }
    }

    public partial class MainViewModel
    {
        private bool _selfFullInfoRequested;

        // -1 until GetPremiumState has answered: the payment options do not change within a session.
        private int _premiumHintDiscount = -1;
        private bool _premiumStateRequested;

        // Loaded once a session, the first time the suggestion is pending; the balance is read live.
        private StarSubscriptions _expiringSubscriptions;
        private bool _expiringSubscriptionsRequested;

        /// <summary>
        /// The one hint shown above the main chat list, picked in a fixed order of precedence.
        /// Null when there is none, or when the frozen account card takes the place.
        /// </summary>
        public ChatListHint GetChatListHint()
        {
            if (ClientService.FreezeState.IsFrozen)
            {
                return null;
            }

            if (ClientService.TryGetSuggestedAction(out SuggestedActionAddLoginPasskey passkey))
            {
                return new ChatListHint(ChatListHintType.AddLoginPasskey, passkey, ClientEx.ParseMarkdown(Strings.PasskeyPopupTitle), ClientEx.ParseMarkdown(Strings.PasskeyPopupText), true);
            }

            if (ClientService.TryGetSuggestedAction(out SuggestedActionExtendPremium extendPremium))
            {
                return new ChatListHint(ChatListHintType.ExtendPremium, extendPremium, ClientEx.ParseMarkdown(Strings.GraceTitle), ClientEx.ParseMarkdown(Strings.GraceMessage), true);
            }

            if (ClientService.TryGetSuggestedAction(out SuggestedActionCustom custom))
            {
                return new ChatListHint(ChatListHintType.Custom, custom, custom.Title, custom.Description, true);
            }

            if (ClientService.TryGetSuggestedAction(out SuggestedActionExtendStarSubscriptions extendStars) && TryGetMissingStarCount(out long missing, out string names))
            {
                var title = Locale.Declension(Strings.R.StarsSubscriptionExpiredHintTitle2, missing, names).ReplaceStar(Icons.Premium);
                return new ChatListHint(ChatListHintType.ExtendStarSubscriptions, extendStars, title.AsFormattedText(), ClientEx.ParseMarkdown(Strings.StarsSubscriptionExpiredHintText), true);
            }

            if (ClientService.IsPremiumAvailable && TryGetBirthdaysToday(out var birthdays))
            {
                string title;
                if (birthdays.Count == 1 && birthdays[0] is MessageSenderUser senderUser && ClientService.TryGetUser(senderUser.UserId, out User user))
                {
                    title = string.Format(Strings.BirthdayTodaySingleTitle, user.FirstName.Length > 0 ? user.FirstName : user.LastName);
                }
                else
                {
                    title = Locale.Declension(Strings.R.BirthdayTodayMultipleTitle, birthdays.Count);
                }

                var subtitle = birthdays.Count == 1
                    ? Strings.BirthdayTodaySingleMessage2
                    : Strings.BirthdayTodayMultipleMessage2;

                return new ChatListHint(ChatListHintType.BirthdaysToday, null, ClientEx.ParseMarkdown(title), ClientEx.ParseMarkdown(subtitle), true, birthdays);
            }

            if (ClientService.HasSuggestedAction(new SuggestedActionSetBirthdate()) && HasNoBirthdate())
            {
                return new ChatListHint(ChatListHintType.SetBirthdate, new SuggestedActionSetBirthdate(), ClientEx.ParseMarkdown(Strings.BirthdaySetupTitle), ClientEx.ParseMarkdown(Strings.BirthdaySetupMessage), true);
            }

            if (ClientService.IsPremiumAvailable && ClientService.HasSuggestedAction(new SuggestedActionGiftPremiumForChristmas()))
            {
                return new ChatListHint(ChatListHintType.GiftPremiumForChristmas, new SuggestedActionGiftPremiumForChristmas(), ClientEx.ParseMarkdown(Strings.GiftPremiumEventAdsTitle), ClientEx.ParseMarkdown(Strings.BoostingPremiumChristmasSubTitle), true);
            }

            // SuggestedActionUpgradePremium is never shown: it needs store billing, and Premium is bought through invoices.
            if (ClientService.IsPremiumAvailable && !ClientService.IsPremium)
            {
                var restore = ClientService.HasSuggestedAction(new SuggestedActionRestorePremium());
                if ((restore || ClientService.HasSuggestedAction(new SuggestedActionSubscribeToAnnualPremium())) && TryGetPremiumHintDiscount(out int discount))
                {
                    if (restore)
                    {
                        return new ChatListHint(ChatListHintType.RestorePremium, new SuggestedActionRestorePremium(), ClientEx.ParseMarkdown(string.Format(Strings.RestorePremiumHintTitle, discount)), ClientEx.ParseMarkdown(Strings.RestorePremiumHintMessage), false);
                    }

                    // The upgrade title does go with the annual message.
                    return new ChatListHint(ChatListHintType.SubscribeToAnnualPremium, new SuggestedActionSubscribeToAnnualPremium(), ClientEx.ParseMarkdown(string.Format(Strings.UpgradePremiumTitle, discount)), ClientEx.ParseMarkdown(Strings.SaveOnAnnualPremiumMessage), false);
                }
            }

            if (ClientService.HasSuggestedAction(new SuggestedActionSetProfilePhoto()) && ClientService.TryGetUser(ClientService.Options.MyId, out User self) && self.ProfilePhoto == null)
            {
                var sender = new MessageSenderUser(self.Id);
                return new ChatListHint(ChatListHintType.SetProfilePhoto, new SuggestedActionSetProfilePhoto(), ClientEx.ParseMarkdown(Strings.HintAddYourPhoto), ClientEx.ParseMarkdown(Strings.HintAddYourPhotoText), true, new MessageSender[] { sender });
            }

            return null;
        }

        /// <summary>
        /// TDLib's close birthdays span a few days around today; the hint is only about today, by the local date.
        /// </summary>
        private bool TryGetBirthdaysToday(out IList<MessageSender> senders)
        {
            senders = null;

            var today = DateTime.Now;

            foreach (var item in ClientService.CloseBirthdayUsers)
            {
                if (item.Birthdate.Day == today.Day && item.Birthdate.Month == today.Month && ClientService.TryGetUser(item.UserId, out User _))
                {
                    senders ??= new List<MessageSender>();
                    senders.Add(new MessageSenderUser(item.UserId));
                }
            }

            return senders != null;
        }

        private bool TryGetMissingStarCount(out long missing, out string names)
        {
            if (_expiringSubscriptions == null)
            {
                if (!_expiringSubscriptionsRequested)
                {
                    _expiringSubscriptionsRequested = true;
                    LoadExpiringSubscriptions();
                }

                missing = 0;
                names = null;
                return false;
            }

            missing = GetExpiringSubscriptionsCost(out names, out _) - ClientService.OwnedStarCount.StarCount;
            return missing > 0;
        }

        private async void LoadExpiringSubscriptions()
        {
            var response = await ClientService.SendAsync(new GetStarSubscriptions(true, string.Empty));
            if (response is StarSubscriptions subscriptions)
            {
                _expiringSubscriptions = subscriptions;

                // No update follows the answer, so the page is told to pick the hint again.
                RaisePropertyChanged(nameof(GetChatListHint));
            }
            else
            {
                _expiringSubscriptionsRequested = false;
            }
        }

        // A subscription counts only when its chat is known, since the chat's name goes in the title.
        private long GetExpiringSubscriptionsCost(out string names, out long firstChatId)
        {
            var cost = 0L;
            var builder = new StringBuilder();

            firstChatId = 0;

            foreach (var subscription in _expiringSubscriptions.Subscriptions)
            {
                if (firstChatId == 0)
                {
                    firstChatId = subscription.ChatId;
                }

                if (ClientService.TryGetChat(subscription.ChatId, out Chat chat))
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(chat.Title);
                    cost += subscription.Pricing.StarCount;
                }
            }

            names = builder.ToString();
            return cost;
        }

        private void ExtendStarSubscriptions()
        {
            if (_expiringSubscriptions != null)
            {
                var cost = GetExpiringSubscriptionsCost(out string names, out long firstChatId);
                NavigationService.ShowPopup(new BuyPopup(), BuyStarsArgs.ForKeepSubscription(cost, names, firstChatId));
            }
        }

        private bool TryGetPremiumHintDiscount(out int discount)
        {
            discount = _premiumHintDiscount;

            if (discount < 0 && !_premiumStateRequested)
            {
                _premiumStateRequested = true;
                LoadPremiumHintDiscount();
            }

            return discount > 0;
        }

        private async void LoadPremiumHintDiscount()
        {
            var response = await ClientService.SendAsync(new GetPremiumState());
            if (response is PremiumState state)
            {
                _premiumHintDiscount = GetAnnualDiscount(state.PaymentOptions);

                // No update follows the answer, so the page is told to pick the hint again.
                RaisePropertyChanged(nameof(GetChatListHint));
            }
            else
            {
                _premiumStateRequested = false;
            }
        }

        // What the annual option saves against twelve monthly payments, in whole percent rounded down.
        private static int GetAnnualDiscount(Vector<PremiumStatePaymentOption> options)
        {
            var monthly = 0d;
            var annual = 0d;

            foreach (var option in options)
            {
                if (option.PaymentOption.MonthCount == 1)
                {
                    monthly = option.PaymentOption.Amount;
                }
                else if (option.PaymentOption.MonthCount == 12)
                {
                    annual = option.PaymentOption.Amount / 12d;
                }
            }

            if (monthly <= 0 || annual <= 0)
            {
                return 0;
            }

            return Math.Max((int)((1 - annual / monthly) * 100), 0);
        }

        private bool HasNoBirthdate()
        {
            if (ClientService.TryGetUserFull(ClientService.Options.MyId, out UserFullInfo fullInfo))
            {
                return fullInfo.Birthdate == null;
            }

            // The hint waits for the own full info, and its UpdateUserFullInfo brings the hint back here.
            if (!_selfFullInfoRequested)
            {
                _selfFullInfoRequested = true;
                ClientService.Send(new GetUserFullInfo(ClientService.Options.MyId));
            }

            return false;
        }

        public void OpenChatListHint(ChatListHint hint)
        {
            switch (hint.Type)
            {
                case ChatListHintType.AddLoginPasskey:
                    AddLoginPasskey();
                    break;
                case ChatListHintType.ExtendPremium when hint.Action is SuggestedActionExtendPremium extendPremium:
                    MessageHelper.OpenUrl(ClientService, NavigationService, extendPremium.ManagePremiumSubscriptionUrl);
                    break;
                case ChatListHintType.Custom when hint.Action is SuggestedActionCustom custom:
                    MessageHelper.OpenUrl(ClientService, NavigationService, custom.Url);
                    break;
                case ChatListHintType.ExtendStarSubscriptions:
                    ExtendStarSubscriptions();
                    break;
                case ChatListHintType.BirthdaysToday:
                    GiftBirthdays(hint.Users);
                    break;
                case ChatListHintType.SetBirthdate:
                    SetBirthdate();
                    break;
                case ChatListHintType.GiftPremiumForChristmas:
                    GiftHelper.ChooseRecipient(ClientService, NavigationService);
                    break;
                case ChatListHintType.RestorePremium:
                case ChatListHintType.SubscribeToAnnualPremium:
                    NavigationService.ShowPopup(new PromoPopup(), new PromoArgs(null, true));
                    ClientService.Send(new HideSuggestedAction(hint.Action));
                    break;
                case ChatListHintType.SetProfilePhoto:
                    SetProfilePhoto();
                    break;
            }
        }

        public void HideChatListHint(ChatListHint hint)
        {
            if (hint.Type == ChatListHintType.BirthdaysToday)
            {
                ClientService.Send(new HideContactCloseBirthdays());
            }
            else if (hint.Action != null)
            {
                ClientService.Send(new HideSuggestedAction(hint.Action));
            }

            switch (hint.Type)
            {
                case ChatListHintType.BirthdaysToday:
                case ChatListHintType.GiftPremiumForChristmas:
                    ShowToast(Strings.BoostingPremiumChristmasToast, ToastPopupIcon.Gift);
                    break;
                case ChatListHintType.SetBirthdate:
                    ShowBirthdateLaterToast();
                    break;
            }
        }

        private async void ShowBirthdateLaterToast()
        {
            var confirm = await ToastPopup.ShowActionAsync(XamlRoot, Strings.BirthdaySetupLater, Strings.Settings, ToastPopupIcon.Info);
            if (confirm == ModalPopupResult.Primary)
            {
                NavigationService.Navigate(typeof(SettingsProfilePage));
            }
        }

        private async void AddLoginPasskey()
        {
            if (!PasskeyManager.IsSupported())
            {
                ShowPopup(Strings.PasskeyNotSupportedText, Strings.AppName, Strings.OK);
                return;
            }

            var confirm = await ShowPopupAsync(new SettingsPasskeysIntroPopup());
            if (confirm != ModalPopupResult.Primary)
            {
                return;
            }

            var response = await PasskeyManager.AddLoginAsync(Window, ClientService);
            if (response is Passkey passkey)
            {
                ClientService.Send(new HideSuggestedAction(new SuggestedActionAddLoginPasskey()));
                ShowToast(string.Format("**{0}**\n{1}", Strings.PasskeyAddedTitle, string.Format(Strings.PasskeyAddedText, passkey.Name)));
            }
            else if (response is Error { Code: not -2147023673 and not -2146893770 } error)
            {
                ShowToast(error);
            }
        }

        private async void GiftBirthdays(IList<MessageSender> users)
        {
            if (users.Count == 1 && users[0] is MessageSenderUser senderUser && ClientService.TryGetUser(senderUser.UserId, out User user))
            {
                ClientService.TryGetUserFull(user.Id, out UserFullInfo fullInfo);
                fullInfo ??= await ClientService.SendAsync(new GetUserFullInfo(user.Id)) as UserFullInfo;

                if (fullInfo != null)
                {
                    ShowPopup(new GiftPopup(ClientService, NavigationService, user, fullInfo));
                }
            }
            else
            {
                // TODO: the recipient picker lists contacts with birthdays today, yesterday and tomorrow first.
                GiftHelper.ChooseRecipient(ClientService, NavigationService);
            }
        }

        private async void SetProfilePhoto()
        {
            if (await _profilePhotoService.SetPhotoAsync(NavigationService, null))
            {
                ClientService.Send(new HideSuggestedAction(new SuggestedActionSetProfilePhoto()));
            }
        }

        public async void SetBirthdate()
        {
            async Task<UserPrivacySettingRule> GetPrimaryRuleAsync()
            {
                var response = await ClientService.SendAsync(new GetUserPrivacySettingRules(new UserPrivacySettingShowBirthdate()));
                if (response is UserPrivacySettingRules rules)
                {
                    foreach (var rule in rules.Rules)
                    {
                        return rule;
                    }
                }

                return null;
            }

            var rule = await GetPrimaryRuleAsync();
            var popup = new SettingsBirthdatePopup(date: null, rule);

            var confirm = await ShowPopupAsync(popup);
            if (confirm == ModalPopupResult.Primary)
            {
                ClientService.Send(new HideSuggestedAction(new SuggestedActionSetBirthdate()));

                var response = await ClientService.SendAsync(new SetBirthdate(popup.Value));
                if (response is Ok)
                {
                    ShowToast(string.Format("**{0}**\n{1}", Strings.PrivacyBirthdaySetDone, Strings.PrivacyBirthdaySetDoneInfo), ToastPopupIcon.Gift);
                }
                else if (response is Error { Code: 429 })
                {
                    ShowPopup(Strings.PrivacyBirthdayTooOftenMessage, Strings.PrivacyBirthdayTooOftenTitle, Strings.OK);
                }
                else if (response is Error error)
                {
                    ShowToast(error);
                }
            }
            else if (popup.ShowPrivacySettings)
            {
                NavigationService.Navigate(typeof(SettingsPrivacyShowBirthdatePage));
            }
        }
    }
}
