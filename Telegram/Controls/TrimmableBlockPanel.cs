//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Navigation;
using Telegram.Services;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls
{
    /// <summary>
    /// The blocks of an expandable quote, stacked, and cut to the height of <see cref="MaxLines"/>
    /// lines of its text until it is expanded. A cut through a text moves up to the end of its
    /// last whole line; through anything else it is a straight edge.
    /// </summary>
    public partial class TrimmableBlockPanel : Panel, ITrimmableText
    {
        private int _maxLines;

        // What the quote collapses to again once expanded, which sets MaxLines to 0.
        private int _trimLines;

        private double _fullHeight;
        private double _limit;

        // One line of the first text, and what it was read from: the read allocates, and the
        // answer only changes with the width or with the size of the text.
        private double _lineHeight;
        private double _lineWidth = double.NaN;
        private UIElement _lineSource;
        private double _lineSourceHeight;

        private RectangleGeometry _clip;
        private double _clipWidth;
        private double _clipHeight;

        // The children from here on are below the cut, and out of the automation tree.
        private int _hiddenFrom = int.MaxValue;

        public int MaxLines
        {
            get => _maxLines;
            set
            {
                if (_maxLines != value)
                {
                    _maxLines = value;
                    _trimLines = value > 0 ? value : _trimLines;

                    InvalidateMeasure();
                }
            }
        }

        public bool IsTextTrimmable { get; private set; }

        public event EventHandler IsTextTrimmableChanged;

        private bool IsCollapsed => _maxLines > 0 && IsTextTrimmable;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = 0d;
            var height = 0d;

            foreach (var child in Children)
            {
                child.Measure(new Size(availableSize.Width, float.PositiveInfinity));

                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;
            }

            _fullHeight = height;
            _limit = _trimLines * GetLineHeight(availableSize.Width);

            // Half a pixel: lines that exactly fill the limit add up to it give or take rounding.
            var trimmable = _trimLines > 0 && height > _limit + 0.5;
            if (IsTextTrimmable != trimmable)
            {
                IsTextTrimmable = trimmable;
                IsTextTrimmableChanged?.Invoke(this, EventArgs.Empty);
            }

            if (IsCollapsed)
            {
                height = _limit;
            }

            return new Size((float)width, (float)height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var y = 0d;

            foreach (var child in Children)
            {
                var height = child.DesiredSize.Height;
                child.Arrange(new Rect(0, (float)y, finalSize.Width, height));

                y += height;
            }

            // After the children: a text answers where its lines are at the width it was given.
            if (IsCollapsed)
            {
                UpdateClip(finalSize.Width);
            }
            else if (Clip != null)
            {
                Clip = null;
                _clipWidth = _clipHeight = 0;

                UpdateAccessibility(int.MaxValue);
            }

            return finalSize;
        }

        private void UpdateClip(double width)
        {
            if (Clip != null && _clipWidth == width && _clipHeight == _fullHeight)
            {
                return;
            }

            _clipWidth = width;
            _clipHeight = _fullHeight;

            var cut = GetCut(_limit, out int hiddenFrom);

            _clip ??= new RectangleGeometry();
            _clip.Rect = new Rect(0, 0, (float)width, (float)cut);
            Clip = _clip;

            UpdateAccessibility(hiddenFrom);
        }

        private double GetCut(double limit, out int hiddenFrom)
        {
            var y = 0d;

            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                var height = child.DesiredSize.Height;

                if (y + height <= limit)
                {
                    y += height;
                    continue;
                }

                hiddenFrom = i + 1;

                if (child is ISelectableControl text && child is FrameworkElement element)
                {
                    var top = y + element.Margin.Top;
                    var bottom = -1d;

                    var lines = text.GetHighlightRectangles(0, text.ContentLength);

                    for (int j = 0; j < lines?.Count; j++)
                    {
                        var line = top + lines[j].Y + lines[j].Height;
                        if (line <= limit + 0.5)
                        {
                            bottom = Math.Max(bottom, line);
                        }
                    }

                    if (bottom >= 0)
                    {
                        return bottom;
                    }

                    // Not even its first line fits, so the cut goes above it - unless nothing
                    // would be left to show.
                    if (y > 0)
                    {
                        hiddenFrom = i;
                        return y;
                    }
                }

                return limit;
            }

            hiddenFrom = Children.Count;
            return limit;
        }

        private double GetLineHeight(double width)
        {
            if (_lineSource != null && _lineWidth == width && _lineSourceHeight == _lineSource.DesiredSize.Height)
            {
                return _lineHeight;
            }

            foreach (var child in Children)
            {
                if (child is ISelectableControl { ContentLength: > 0 } text)
                {
                    var lines = text.GetHighlightRectangles(0, 1);
                    if (lines?.Count > 0 && lines[0].Height > 0)
                    {
                        _lineSource = child;
                        _lineSourceHeight = child.DesiredSize.Height;
                        _lineWidth = width;

                        return _lineHeight = lines[0].Height;
                    }

                    break;
                }
            }

            // A quote with no text to measure: a line of message text, which the UI font sets
            // about a third taller than its size.
            _lineSource = null;
            return AppSettings.Appearance.MessageFontSize * BootStrapper.Current.TextScaleFactor * 4 / 3;
        }

        private void UpdateAccessibility(int hiddenFrom)
        {
            if (hiddenFrom == _hiddenFrom)
            {
                return;
            }

            var first = Math.Min(hiddenFrom, _hiddenFrom);

            for (int i = first; i < Children.Count; i++)
            {
                if (i >= hiddenFrom)
                {
                    AutomationProperties.SetAccessibilityView(Children[i], AccessibilityView.Raw);
                }
                else
                {
                    Children[i].ClearValue(AutomationProperties.AccessibilityViewProperty);
                }
            }

            _hiddenFrom = hiddenFrom;
        }
    }
}
