//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.ComponentModel;
using Telegram.Common;
using Telegram.Navigation.Services;
using Telegram.Navigation;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views.Chats;
using Telegram.Views.Profile;
using Windows.UI.Composition;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Views
{
    public sealed partial class RevenuePage : HostedPage, INavigablePage
    {
        public RevenueViewModel ViewModel => DataContext as RevenueViewModel;

        private CompositionPropertySet _properties;

        private readonly ScrollViewerIncrementalLoader _loader;

        public RevenuePage()
        {
            InitializeComponent();
            Transition = PageTransition.Entrance;

            Title = Strings.Monetization;
        }

        public void OnBackRequested(BackRequestedRoutedEventArgs args)
        {
            if (MediaFrame.Content is ProfileTabPage tabPage)
            {
                tabPage.OnBackRequested(args);
            }
        }

        protected override void OnNavigatedTo(NavigatedEventArgs e)
        {
            ViewModel.PropertyChanged += OnPropertyChanged;

            if (ViewModel.SelectedItem is RevenueTabItem tab)
            {
                MediaFrame.Navigate(tab.Type, null, PageTransition.None);
            }

            if (ViewModel.ClientService.TryGetChat((long)ViewModel.NavigationService.CurrentPageParam, out Chat chat))
            {
                Title = chat.Title;
            }
        }

        protected override void OnNavigatedFrom(NavigatedEventArgs e)
        {
            ViewModel.PropertyChanged -= OnPropertyChanged;
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName.Equals("SharedCount") && ViewModel.SelectedItem is RevenueTabItem tab)
            {
                MediaFrame.Navigate(tab.Type, null, PageTransition.None);
            }
        }

        private void OnNavigating(object sender, NavigatingEventArgs e)
        {
        }

        private void OnNavigated(object sender, NavigatedEventArgs e)
        {
            if (e.Content is ChatStatisticsPage statistics)
            {
                statistics.DataContext = ViewModel.Statistics;
            }
            else if (e.Content is ChatBoostsPage boosts)
            {
                boosts.DataContext = ViewModel.Boosts;
            }
            else if (e.Content is ChatRevenuePage revenue)
            {
                revenue.DataContext = ViewModel.Revenue;
            }
        }

        private int _prevSelectedIndex = -1;

        private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Navigation.SelectedItem is RevenueTabItem page && page.Type != MediaFrame.Content?.GetType())
            {
                Logger.Info(page.Type);

                var transition = _prevSelectedIndex == -1
                    ? PageTransition.None
                    : _prevSelectedIndex < Navigation.SelectedIndex
                    ? PageTransition.Forward
                    : PageTransition.Backward;

                _prevSelectedIndex = Navigation.SelectedIndex;
                MediaFrame.Navigate(page.Type, null, transition);
            }
        }
    }
}
