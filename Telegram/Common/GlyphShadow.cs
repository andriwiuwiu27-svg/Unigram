//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Navigation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Common
{
    /// <summary>
    /// The drop shadow of a glyph in a <see cref="TextBlock"/>, cast from another element so that
    /// it can sit behind the text: the glyph's box stretched over the target, blurred.
    /// </summary>
    /// <remarks>
    /// The mask is an <see cref="OutlineMask"/> of the glyph, never <c>TextBlock.GetAlphaMask</c>,
    /// which can fail-fast the process when the device is lost.
    /// </remarks>
    public sealed partial class GlyphShadow
    {
        // A compositor belongs to its thread, and one brush can mask any number of shadows, so
        // every caster of a glyph on this thread shares one mask.
        [ThreadStatic]
        private static Dictionary<MaskKey, CompositionSurfaceBrush> _masks;

        private readonly SpriteVisual _visual;
        private readonly DropShadow _shadow;

        private string _glyph;

        public GlyphShadow(UIElement target, float radius = 20, float opacity = 0.25f)
        {
            var compositor = BootStrapper.Current.Compositor;

            _shadow = compositor.CreateDropShadow();
            _shadow.BlurRadius = radius;
            _shadow.Opacity = opacity;
            _shadow.Color = Colors.Black;

            _visual = compositor.CreateSpriteVisual();
            _visual.Shadow = _shadow;
            _visual.RelativeSizeAdjustment = Vector2.One;
            _visual.IsVisible = false;

            ElementCompositionPreview.SetElementChildVisual(target, _visual);
        }

        /// <param name="source">The text block that draws <paramref name="glyph"/>, read for its font.</param>
        public void Show(string glyph, TextBlock source)
        {
            if (glyph == _glyph)
            {
                return;
            }

            _glyph = glyph;
            _shadow.Mask = GetMask(new MaskKey(glyph, source.FontFamily.Source, (float)source.FontSize));
            _visual.IsVisible = true;
        }

        public void Hide()
        {
            if (_glyph == null)
            {
                return;
            }

            _glyph = null;
            _visual.IsVisible = false;
        }

        private static CompositionSurfaceBrush GetMask(MaskKey key)
        {
            _masks ??= new Dictionary<MaskKey, CompositionSurfaceBrush>();

            if (_masks.TryGetValue(key, out CompositionSurfaceBrush mask))
            {
                return mask;
            }

            var compositor = BootStrapper.Current.Compositor;

            using var format = new CanvasTextFormat
            {
                FontFamily = AppPaths.GetCanvasFontFamily(key.Family),
                FontSize = key.Size,
                WordWrapping = CanvasWordWrapping.NoWrap
            };

            // Rooted with using: Win2D's UWP projection has no GC.KeepAlive, so a geometry whose
            // only reference is a temporary can be finalized while CompositionPath is reading it.
            using var layout = new CanvasTextLayout(ElementComposition.GetSharedDevice(), key.Glyph, format, 0, 0);
            using var outline = CanvasGeometry.CreateText(layout);

            // The layout box is what TextBlock measures to, and so the box an alpha mask covered.
            var bounds = layout.LayoutBounds;
            var offset = new Vector2((float)bounds.X, (float)bounds.Y);
            var size = new Vector2((float)bounds.Width, (float)bounds.Height);

            var outlineMask = new OutlineMask(compositor);
            var shape = outlineMask.Add(compositor.CreatePathGeometry(new CompositionPath(outline)));
            shape.Offset = -offset;
            outlineMask.Size = size;

            mask = outlineMask.Brush;
            _masks[key] = mask;

            return mask;
        }

        private readonly struct MaskKey : IEquatable<MaskKey>
        {
            public readonly string Glyph;
            public readonly string Family;
            public readonly float Size;

            public MaskKey(string glyph, string family, float size)
            {
                Glyph = glyph;
                Family = family;
                Size = size;
            }

            public bool Equals(MaskKey other)
            {
                return Glyph == other.Glyph && Family == other.Family && Size == other.Size;
            }

            public override bool Equals(object obj)
            {
                return obj is MaskKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Glyph, Family, Size);
            }
        }
    }
}
