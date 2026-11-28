//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Telegram.Common;
using Telegram.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    public sealed partial class SettingsSuggestionCard : UserControl
    {
        public SettingsSuggestionCard()
        {
            InitializeComponent();
        }

        public SettingsSuggestion Suggestion { get; private set; }

        public event RoutedEventHandler NoClick
        {
            add => No.Click += value;
            remove => No.Click -= value;
        }

        public event RoutedEventHandler YesClick
        {
            add => Yes.Click += value;
            remove => Yes.Click -= value;
        }

        public void Update(SettingsSuggestion suggestion)
        {
            Suggestion = suggestion;

            TextBlockHelper.SetFormattedText(Title, suggestion.Title);
            TextBlockHelper.SetFormattedText(Text, suggestion.Text);

            var no = suggestion.NoText != null;

            No.Content = suggestion.NoText;
            No.Visibility = no
                ? Visibility.Visible
                : Visibility.Collapsed;

            Yes.Content = suggestion.YesText;

            // Alone, the one button takes the whole row.
            Grid.SetColumn(Yes, no ? 1 : 0);
            Grid.SetColumnSpan(Yes, no ? 1 : 2);
        }
    }
}
