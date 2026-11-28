//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Td.Api;
using Telegram.Views.Supergroups.Popups;
using Windows.UI.Xaml.Controls;

namespace Telegram.ViewModels
{
    public partial class DialogViewModel
    {
        // Asked at most once a week per account, whatever the answer was: only leaving the group as
        // it is hides the suggestion for good.
        private const long BroadcastGroupSuggestionInterval = 7 * 24 * 60 * 60;

        // Armed when a chat opens and spent on its first slice, so that the slices a reply, a search or
        // a date jumps to never reach the checks.
        private bool _broadcastGroupSuggestionDue;

        private void ShowBroadcastGroupSuggestion(Chat chat)
        {
            if (!_broadcastGroupSuggestionDue)
            {
                return;
            }

            _broadcastGroupSuggestionDue = false;

            if (!ClientService.IsBroadcastGroupSuggested || !CanSuggestBroadcastGroup(chat, out long supergroupId))
            {
                return;
            }

            var now = DateTime.Now.ToUnixTimeSeconds();
            if (Math.Abs(now - Settings.BroadcastGroupSuggestionDate) < BroadcastGroupSuggestionInterval || ModalPopup.IsAnyPopupOpen(XamlRoot))
            {
                return;
            }

            Settings.BroadcastGroupSuggestionDate = now;
            SuggestBroadcastGroup(supergroupId);
        }

        private async void SuggestBroadcastGroup(long supergroupId)
        {
            var learnMore = await ShowPopupAsync(Strings.GigagroupAlertText, Strings.GigagroupAlertTitle, Strings.GigagroupAlertLearnMore, Strings.Cancel);
            if (learnMore != ModalPopupResult.Primary)
            {
                ShowToast(Strings.GigagroupConvertCancelHint, ToastPopupIcon.Info);
                return;
            }

            var convert = await ShowPopupAsync(new SupergroupBroadcastGroupPopup());
            if (convert == ModalPopupResult.Secondary)
            {
                ShowToast(Strings.GigagroupConvertCancelHint, ToastPopupIcon.Info);
                ClientService.Send(new HideSuggestedAction(new SuggestedActionConvertToBroadcastGroup(supergroupId)));
                return;
            }
            else if (convert != ModalPopupResult.Primary)
            {
                return;
            }

            var confirm = await ShowPopupAsync(Strings.GigagroupConvertAlertText, Strings.GigagroupConvertAlertTitle, Strings.GigagroupConvertAlertConver, Strings.Cancel, destructive: true);
            if (confirm != ModalPopupResult.Primary)
            {
                return;
            }

            var response = await ClientService.SendAsync(new ToggleSupergroupIsBroadcastGroup(supergroupId));
            if (response is Ok)
            {
                ShowToast(Strings.BroadcastGroupConvertSuccess, ToastPopupIcon.Success);
            }
            else if (response is Error error)
            {
                ShowToast(error);
            }
        }

        private bool CanSuggestBroadcastGroup(Chat chat, out long supergroupId)
        {
            supergroupId = 0;

            if (chat.Type is not ChatTypeSupergroup { IsChannel: false } super)
            {
                return false;
            }

            if (!ClientService.TryGetSupergroup(super.SupergroupId, out Supergroup supergroup) || supergroup.Status is not ChatMemberStatusCreator || supergroup.IsBroadcastGroup)
            {
                return false;
            }

            supergroupId = super.SupergroupId;
            return ClientService.HasSuggestedAction(new SuggestedActionConvertToBroadcastGroup(supergroupId));
        }
    }
}
