//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Td.Api;
using Telegram.Views.Settings;
using Telegram.Views.Settings.LoginEmail;
using Telegram.Views.Settings.Popups;
using Windows.UI.Xaml.Controls;

namespace Telegram.ViewModels
{
    public partial class MainViewModel
    {
        // The suggestions the main screen opens as modals: one at a time, each at most once per run.
        private bool _suggestionShowing;

        private bool _loginEmailShown;
        private bool _passwordShown;
        private bool _archiveShown;

        public async void ShowNextSuggestion()
        {
            if (_suggestionShowing || XamlRoot == null)
            {
                return;
            }

            _suggestionShowing = true;

            if (!_loginEmailShown && ClientService.TryGetSuggestedAction(out SuggestedActionSetLoginEmailAddress loginEmail))
            {
                _loginEmailShown = true;
                await SetLoginEmailAddressAsync(loginEmail);
            }
            else if (!_passwordShown && ClientService.TryGetSuggestedAction(out SuggestedActionSetPassword password))
            {
                _passwordShown = true;
                await SetPasswordAsync(password);
            }
            else if (!_archiveShown && ClientService.TryGetSuggestedAction(out SuggestedActionEnableArchiveAndMuteNewChats archive))
            {
                _archiveShown = true;
                await EnableArchiveAndMuteNewChatsAsync(archive);
            }
            else
            {
                _suggestionShowing = false;
                return;
            }

            _suggestionShowing = false;
            ShowNextSuggestion();
        }

        private async Task SetLoginEmailAddressAsync(SuggestedActionSetLoginEmailAddress action)
        {
            // When it cannot be hidden, TDLib's contract is that the app stays unusable until an
            // address is set, so every way out of the popup leads back into it. Until the window goes.
            while (XamlRoot != null)
            {
                var address = new SettingsLoginEmailAddressPopup(ClientService, action.CanBeHidden);

                var confirm = await ShowPopupAsync(address);
                if (confirm == ModalPopupResult.Primary && address.CodeInfo != null)
                {
                    var code = new SettingsLoginEmailCodePopup(ClientService, address.CodeInfo);

                    var verified = await ShowPopupAsync(code);
                    if (verified == ModalPopupResult.Primary)
                    {
                        ShowToast(Strings.YourLoginEmailChangedSuccess, ToastPopupIcon.Success);
                        return;
                    }
                }
                else if (action.CanBeHidden)
                {
                    ClientService.Send(new HideSuggestedAction(action));
                    return;
                }
            }
        }

        private async Task SetPasswordAsync(SuggestedActionSetPassword action)
        {
            await NavigationService.NavigateToPasswordSetupAsync(action.AuthorizationDelay);

            // Offered once, set or not: setting a password removes it anyway. TDLib wants the received
            // object back, delay included.
            ClientService.Send(new HideSuggestedAction(action));
        }

        private async Task EnableArchiveAndMuteNewChatsAsync(SuggestedActionEnableArchiveAndMuteNewChats action)
        {
            var confirm = await ShowPopupAsync(Strings.HideNewChatsAlertText, Strings.HideNewChatsAlertTitle, Strings.GoToSettings, Strings.Cancel);
            if (confirm == ModalPopupResult.Primary)
            {
                // The setting is in the archive settings, which open from the privacy page.
                NavigationService.Navigate(typeof(SettingsPrivacyAndSecurityPage));
                ShowPopup(new SettingsArchivePopup(ClientService));
            }

            ClientService.Send(new HideSuggestedAction(action));
        }
    }
}
