//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Windows.UI.Xaml.Navigation;

namespace Telegram.Navigation.Services
{
    public partial class NavigatedEventArgs : EventArgs
    {
        public NavigationMode NavigationMode { get; set; }

        public PageTransition Transition { get; set; }

        public Type SourcePageType { get; set; }

        public object Parameter { get; set; }

        public NavigationPage Content { get; set; }

        public double VerticalOffset { get; set; }
    }
}
