//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.Geometry;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Telegram.Navigation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Common
{
    /// <summary>
    /// The drop shadow of a <see cref="TextBlock"/>'s text, cast from another element so that it
    /// can sit behind the text. That element has to cover the text block exactly, which a sibling
    /// in the same grid cell does: the mask is stretched over it.
    /// </summary>
    /// <remarks>
    /// The mask is an <see cref="OutlineMask"/> of the text laid out again from the block's runs,
    /// not the block's alpha mask, and so it is not live: whoever changes the text calls
    /// <see cref="Update"/>. Read are each run's family, size, weight and style, line breaks and
    /// the block's alignment. There is no wrapping or trimming.
    /// </remarks>
    public sealed partial class TextShadow
    {
        private static readonly Dictionary<string, bool> _installed = new();

        private readonly SpriteVisual _visual;
        private readonly OutlineMask _mask;
        private readonly CompositionPathGeometry _geometry;
        private readonly CompositionSpriteShape _shape;

        private readonly StringBuilder _builder = new();

        private string _text;
        private string _family;
        private double _size;
        private ushort _weight;
        private FontStyle _style;
        private TextAlignment _alignment;

        public TextShadow(UIElement target, float radius = 20, float opacity = 0.25f, Color? color = null, Vector3 offset = default)
        {
            var compositor = BootStrapper.Current.Compositor;

            _mask = new OutlineMask(compositor);
            _geometry = compositor.CreatePathGeometry();
            _shape = _mask.Add(_geometry);

            var shadow = compositor.CreateDropShadow();
            shadow.BlurRadius = radius;
            shadow.Opacity = opacity;
            shadow.Color = color ?? Colors.Black;
            shadow.Offset = offset;
            shadow.Mask = _mask.Brush;

            _visual = compositor.CreateSpriteVisual();
            _visual.Shadow = shadow;
            _visual.RelativeSizeAdjustment = Vector2.One;
            _visual.IsVisible = false;

            ElementCompositionPreview.SetElementChildVisual(target, _visual);
        }

        public SpriteVisual Visual => _visual;

        public void Update(TextBlock source)
        {
            _builder.Clear();
            AppendText(source.Inlines);

            var text = _builder.ToString();
            var family = source.FontFamily?.Source;

            if (text == _text
                && family == _family
                && source.FontSize == _size
                && source.FontWeight.Weight == _weight
                && source.FontStyle == _style
                && source.TextAlignment == _alignment)
            {
                return;
            }

            _text = text;
            _family = family;
            _size = source.FontSize;
            _weight = source.FontWeight.Weight;
            _style = source.FontStyle;
            _alignment = source.TextAlignment;

            if (text.Length == 0)
            {
                _geometry.Path = null;
                _visual.IsVisible = false;
                return;
            }

            using var format = new CanvasTextFormat
            {
                FontFamily = ResolveFamily(family),
                FontSize = (float)source.FontSize,
                FontWeight = source.FontWeight,
                FontStyle = source.FontStyle,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = source.TextAlignment switch
                {
                    TextAlignment.Center => CanvasHorizontalAlignment.Center,
                    TextAlignment.Right => CanvasHorizontalAlignment.Right,
                    _ => CanvasHorizontalAlignment.Left
                }
            };

            // Rooted with using: Win2D's UWP projection has no GC.KeepAlive, so a geometry whose
            // only reference is a temporary can be finalized while CompositionPath is reading it.
            using var layout = new CanvasTextLayout(ElementComposition.GetSharedDevice(), text, format, 0, 0);

            var position = 0;
            FormatRuns(layout, source.Inlines, ref position);

            using var outline = CanvasGeometry.CreateText(layout);

            // The layout box is what TextBlock measures to, and so the box an alpha mask covered.
            // Centred or right aligned against no width, it starts left of the origin.
            var bounds = layout.LayoutBounds;

            _geometry.Path = new CompositionPath(outline);
            _shape.Offset = new Vector2(-(float)bounds.X, -(float)bounds.Y);
            _mask.Size = new Vector2((float)bounds.Width, (float)bounds.Height);
            _visual.IsVisible = true;
        }

        private void AppendText(InlineCollection inlines)
        {
            for (int i = 0; i < inlines.Count; i++)
            {
                switch (inlines[i])
                {
                    case Run run:
                        _builder.Append(run.Text);
                        break;
                    case LineBreak:
                        _builder.Append('\n');
                        break;
                    case Span span:
                        AppendText(span.Inlines);
                        break;
                }
            }
        }

        // Walks the inlines as AppendText did, so the positions are the same.
        private static void FormatRuns(CanvasTextLayout layout, InlineCollection inlines, ref int position)
        {
            for (int i = 0; i < inlines.Count; i++)
            {
                switch (inlines[i])
                {
                    case Run run:
                        var length = run.Text.Length;

                        // Only what the run sets itself: a value it inherits is the block's, which
                        // the format already carries.
                        if (length > 0)
                        {
                            if (run.ReadLocalValue(TextElement.FontSizeProperty) != DependencyProperty.UnsetValue)
                            {
                                layout.SetFontSize(position, length, (float)run.FontSize);
                            }

                            if (run.ReadLocalValue(TextElement.FontWeightProperty) != DependencyProperty.UnsetValue)
                            {
                                layout.SetFontWeight(position, length, run.FontWeight);
                            }

                            if (run.ReadLocalValue(TextElement.FontStyleProperty) != DependencyProperty.UnsetValue)
                            {
                                layout.SetFontStyle(position, length, run.FontStyle);
                            }

                            if (run.ReadLocalValue(TextElement.FontFamilyProperty) != DependencyProperty.UnsetValue)
                            {
                                layout.SetFontFamily(position, length, ResolveFamily(run.FontFamily?.Source));
                            }
                        }

                        position += length;
                        break;
                    case LineBreak:
                        position++;
                        break;
                    case Span span:
                        FormatRuns(layout, span.Inlines, ref position);
                        break;
                }
            }
        }

        /// <summary>
        /// The family of a XAML font chain that draws the text, as one Win2D can take: the first one
        /// installed, or the first packaged one.
        /// </summary>
        /// <remarks>
        /// Win2D takes a single family, and one it does not have falls back to the system's for
        /// every character - for the monospace chain, a proportional face. Only a character the
        /// family lacks still parts company with XAML, which would try the chain's next family
        /// before the system's.
        /// </remarks>
        private static string ResolveFamily(string chain)
        {
            if (string.IsNullOrEmpty(chain) || chain.IndexOf(',') < 0)
            {
                return AppPaths.GetCanvasFontFamily(chain);
            }

            var families = chain.Split(',');

            foreach (var entry in families)
            {
                var family = entry.Trim();

                if (family.IndexOf('#') >= 0 || IsInstalled(family))
                {
                    return AppPaths.GetCanvasFontFamily(family);
                }
            }

            return AppPaths.GetCanvasFontFamily(families[0].Trim());
        }

        private static bool IsInstalled(string family)
        {
            lock (_installed)
            {
                if (_installed.TryGetValue(family, out bool installed))
                {
                    return installed;
                }

                using var matching = CanvasFontSet.GetSystemFontSet().GetMatchingFonts(family, FontWeights.Normal, FontStretch.Normal, FontStyle.Normal);

                installed = matching.Fonts.Count > 0;
                _installed[family] = installed;

                return installed;
            }
        }
    }
}
