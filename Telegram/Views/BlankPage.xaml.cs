//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Td.Api;
using Telegram.Views.Authorization;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Views
{
    public sealed partial class BlankPage : NavigationPage
    {
        private IClientService _clientService;
        private INavigationService _navigationService;
        private IEventAggregator _aggregator;

        public BlankPage()
        {
            InitializeComponent();
            DataContext = new object();

            Retention = PageRetention.Keep;
            Transition = PageTransition.None;
        }

        public void Activate(INavigationService navigationService)
        {
            _navigationService = navigationService;
            _clientService ??= navigationService.Session.Resolve<IClientService>();
            _aggregator ??= navigationService.Session.Resolve<IEventAggregator>();
        }

        protected override void OnNavigatedTo(NavigatedEventArgs e)
        {
            if (e.NavigationMode == NavigationMode.Back && _navigationService?.ForwardStack is { Count: > 0 } forward && forward[^1].SourcePageType == typeof(AuthorizationPage))
            {
                _clientService.Send(new Destroy());
            }
            else if (_navigationService != null)
            {
                ResetChatTheme(_navigationService, ActualTheme, _clientService, _aggregator);
            }
        }

        /// <summary>
        /// Puts back the default chat theme and background, which is what the empty detail pane shows.
        /// </summary>
        public static void ResetChatTheme(INavigationService navigationService, ElementTheme theme, IClientService clientService, IEventAggregator aggregator)
        {
            if (navigationService.Window.UpdateChatTheme(theme, null, null, null, null) && clientService != null)
            {
                aggregator.Publish(new UpdateDefaultBackground(false, clientService.GetDefaultBackground(false)));
                aggregator.Publish(new UpdateDefaultBackground(true, clientService.GetDefaultBackground(true)));
            }
        }
    }
}
