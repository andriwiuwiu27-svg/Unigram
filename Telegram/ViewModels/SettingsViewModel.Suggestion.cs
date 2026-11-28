//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.Controls;
using Telegram.Td;
using Telegram.Td.Api;
using Telegram.Views.Settings;
using Telegram.Views.Settings.Password;
using Windows.UI.Xaml.Controls;

namespace Telegram.ViewModels
{
    public enum SettingsSuggestionType
    {
        ExtendPremium,
        CheckPhoneNumber,
        CheckPassword
    }

    public sealed class SettingsSuggestion
    {
        public SettingsSuggestion(SettingsSuggestionType type, SuggestedAction action, FormattedText title, FormattedText text, string noText, string yesText)
        {
            Type = type;
            Action = action;
            Title = title;
            Text = text;
            NoText = noText;
            YesText = yesText;
        }

        public SettingsSuggestionType Type { get; }

        public SuggestedAction Action { get; }

        public FormattedText Title { get; }

        public FormattedText Text { get; }

        /// <summary>
        /// Null when the card has a single button.
        /// </summary>
        public string NoText { get; }

        public string YesText { get; }
    }

    public partial class SettingsViewModel
    {
        private void Handle(UpdateSuggestedActions update)
        {
            BeginOnUIThread(UpdateSuggestion);
        }

        private void UpdateSuggestion()
        {
            Delegate?.UpdateSuggestion(GetSuggestion());
        }

        /// <summary>
        /// The one card shown at the top of Settings, picked in a fixed order of precedence.
        /// </summary>
        private SettingsSuggestion GetSuggestion()
        {
            if (ClientService.TryGetSuggestedAction(out SuggestedActionExtendPremium extendPremium))
            {
                return new SettingsSuggestion(SettingsSuggestionType.ExtendPremium, extendPremium, Strings.GraceSuggestionTitle.AsFormattedText(), Strings.GraceSuggestionMessage.AsFormattedText(), null, Strings.GraceSuggestionButton);
            }

            if (ClientService.TryGetSuggestedAction(out SuggestedActionCheckPhoneNumber checkPhoneNumber) && ClientService.TryGetUser(ClientService.Options.MyId, out User self))
            {
                var text = ClientEx.ParseMarkdown(Strings.CheckPhoneNumberInfo);
                if (text.Entities.Count == 1)
                {
                    text.Entities[0].Type = new TextEntityTypeTextUrl(Strings.CheckPhoneNumberLearnMoreUrl);
                }

                return new SettingsSuggestion(SettingsSuggestionType.CheckPhoneNumber, checkPhoneNumber, string.Format(Strings.CheckPhoneNumber, PhoneNumber.Format(self.PhoneNumber)).AsFormattedText(), text, Strings.CheckPhoneNumberNo, Strings.CheckPhoneNumberYes2);
            }

            if (ClientService.TryGetSuggestedAction(out SuggestedActionCheckPassword checkPassword))
            {
                return new SettingsSuggestion(SettingsSuggestionType.CheckPassword, checkPassword, Strings.YourPasswordHeader.AsFormattedText(), Strings.YourPasswordRemember.AsFormattedText(), Strings.YourPasswordRememberNo, Strings.YourPasswordRememberYes);
            }

            return null;
        }

        public void RefuseSuggestion(SettingsSuggestion suggestion)
        {
            switch (suggestion.Type)
            {
                case SettingsSuggestionType.CheckPhoneNumber:
                    ShowPopup(new ChangePhoneNumberPopup());
                    break;
                case SettingsSuggestionType.CheckPassword:
                    CheckPassword();
                    break;
            }
        }

        public void AcceptSuggestion(SettingsSuggestion suggestion)
        {
            if (suggestion.Action is SuggestedActionExtendPremium extendPremium)
            {
                MessageHelper.OpenUrl(ClientService, NavigationService, extendPremium.ManagePremiumSubscriptionUrl);
            }

            ClientService.Send(new HideSuggestedAction(suggestion.Action));
        }

        private async void CheckPassword()
        {
            var response = await ClientService.SendAsync(new GetPasswordState());
            if (response is not PasswordState { HasPassword: true } passwordState)
            {
                return;
            }

            var popup = new SettingsPasswordConfirmPopup(ClientService, passwordState);

            var confirm = await ShowPopupAsync(popup);
            if (confirm == ModalPopupResult.Primary && !string.IsNullOrEmpty(popup.Password))
            {
                ClientService.Send(new HideSuggestedAction(new SuggestedActionCheckPassword()));
                ShowPopup(Strings.CheckPasswordPerfectInfo, Strings.CheckPasswordPerfect, Strings.CheckPasswordBackToSettings);
            }
            else if (popup.RecoveryEmailAddressCodeInfo != null)
            {
                var emailCode = new SettingsPasswordEmailCodePopup(ClientService, popup.RecoveryEmailAddressCodeInfo, SettingsPasswordEmailCodeType.Recovery);

                if (ModalPopupResult.Primary == await ShowPopupAsync(emailCode))
                {
                    ShowPopup(new SettingsPasswordDonePopup());
                }
            }
        }
    }
}
