//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;

namespace Telegram.Navigation.Services
{
    /// <summary>
    /// SourcePageType and Parameter are the page being navigated to, as they are on the platform's
    /// own arguments; Content is the page being left.
    /// </summary>
    public partial class NavigatingEventArgs : NavigatedEventArgs
    {
        public bool Cancel { get; set; }

        public bool Suspending { get; set; }

        public Type TargetPageType { get; set; }

        public object TargetPageParameter { get; set; }
    }
}
