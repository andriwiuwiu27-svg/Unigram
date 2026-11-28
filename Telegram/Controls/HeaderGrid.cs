//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;

namespace Telegram.Controls
{
    /// <summary>
    /// A single-cell grid whose first child is a header the others make room for. The header is
    /// measured before them and <see cref="HeaderMeasured"/> raised, so a child can be sized from it
    /// in the same pass: from SizeChanged, the first pass would lay it out against a stale size.
    /// </summary>
    public partial class HeaderGrid : Grid
    {
        public event EventHandler HeaderMeasured;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Children.Count > 0)
            {
                // The constraint the grid gives a child of its only cell, so the measure below
                // finds the header clean.
                Children[0].Measure(availableSize);
                HeaderMeasured?.Invoke(this, EventArgs.Empty);
            }

            return base.MeasureOverride(availableSize);
        }
    }
}
