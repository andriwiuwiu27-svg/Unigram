//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Telegram.Common;
using Telegram.Composition;
using Telegram.Navigation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Telegram.Controls.Chats
{
    /// <summary>
    /// The composer's text giving way to its rewrite: a front crosses the field at 42°, the old text
    /// fading and coming apart as dust ahead of it and the new one uncovered behind it, an accent
    /// band trailing it.
    /// </summary>
    /// <remarks>
    /// It lies over the field and reaches above it, so the dust has room to rise. The old text is a
    /// frozen snapshot. The new one is the field itself, live and editable, clipped to what the front
    /// has passed, so nothing has to be hidden or handed back at the end.
    ///
    /// Everything is a handful of sprites on the compositor, whatever the length of the text: the
    /// dust is stripes of the snapshot rather than particles, and the band is the field's own pixels
    /// through one live surface.
    /// </remarks>
    public partial class ComposerRewriteTransition : Grid
    {
        // The front's normal, 42° below the horizontal: it enters at the field's top left corner.
        private static readonly Vector2 Normal = new(0.7431f, 0.6691f);
        private const float Angle = 42;

        private static readonly Vector2 Ease1 = new(0.42f, 0);
        private static readonly Vector2 Ease2 = new(0.22f, 1);

        // Half the old text's soft edge, 22 along a line of text, and how far before the text the
        // front starts and past it it ends, 4 more: the edge enters and leaves whole.
        private static readonly float Edge = 22 * Normal.X;
        private static readonly float Margin = 26 * Normal.X;

        // The band is measured in time behind the front, not in pixels: solid where the front was up to
        // 120 ms ago, gone where it was 280 ms ago, so it is widest where the front is fastest.
        private static readonly TimeSpan BandSolid = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan BandFade = TimeSpan.FromMilliseconds(280);

        // The front arrives this long before the transition ends.
        private static readonly TimeSpan FrontLead = TimeSpan.FromMilliseconds(60);

        // Not read from the reference, whose fade constant did not survive decompiling.
        private static readonly TimeSpan BandFadeOut = TimeSpan.FromMilliseconds(120);

        // A stripe of dust sets off as one, so the narrower they are the closer the dust keeps to the
        // front. Each is drawn once per noise mask with pixels in it, about six, which is what caps them.
        private const float StripeWidth = 24;
        private const int MaxStripes = 8;

        // How long a piece of dust lives and how far it drifts, right and up.
        private const double DustLife = 160;
        private const double DustLifeRange = 200;
        private const float DriftX = 12;
        private const float DriftXRange = 28;
        private const float DriftY = 6;
        private const float DriftYRange = 18;

        // A line of the composer at the default text size, which only the duration is measured in.
        private const float LineHeight = 20;

        private static readonly Random _random = new();

        private FrameworkElement _field;
        private Vector2 _origin;

        private CompositionVisualSurface _snapshot;
        private Vector2 _snapshotSize;

        private Visual _fieldVisual;
        private CompositionClip _fieldClip;

        private ContainerVisual _root;
        private ContainerVisual _old;
        private SpriteVisual _band;
        private CompositionVisualSurface _bandSurface;
        private CompositionScopedBatch _batch;

        public ComposerRewriteTransition()
        {
            // The masks are encoded asynchronously, and the first rewrite must not find them missing.
            CompositionDustLayers.Prepare();
        }

        #region Accent

        public Brush Accent
        {
            get => (Brush)GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }

        public static readonly DependencyProperty AccentProperty =
            DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(ComposerRewriteTransition), new PropertyMetadata(null));

        #endregion

        /// <summary>
        /// Takes the snapshot of the old text, which lands on the next frame: the field must not
        /// change before then. False when the transition is not played at all.
        /// </summary>
        public bool Capture(FrameworkElement field)
        {
            Stop();

            if (!PowerSavingPolicy.AreMaterialsEnabled
                || !PowerSavingPolicy.AreSmoothTransitionsEnabled
                || !IsLoaded)
            {
                return false;
            }

            _snapshot = CompositionDustVisual.CreateSnapshot(field, out _snapshotSize);

            if (_snapshot == null)
            {
                return false;
            }

            _field = field;
            _origin = field.TransformToVisual(this).TransformVector2();
            return true;
        }

        /// <summary>
        /// Plays the transition over the field, which already holds the new text, and returns how
        /// long it lasts.
        /// </summary>
        public TimeSpan Play()
        {
            var field = _field;
            var snapshot = _snapshot;

            if (field == null || snapshot == null)
            {
                return TimeSpan.Zero;
            }

            // The new text is not laid out yet, and both where the field is and how long this lasts
            // depend on its height.
            field.UpdateLayout();

            var size = field.ActualSize;
            var origin = field.TransformToVisual(this).TransformVector2();

            var width = MathF.Max(_snapshotSize.X, size.X);
            var height = MathF.Max(_snapshotSize.Y, size.Y);

            // 600 ms for a line, 60 more for each further one, up to 900.
            var lines = 1 + Math.Max(0, (int)MathF.Round((height - (float)field.MinHeight) / LineHeight));
            var duration = TimeSpan.FromMilliseconds(Math.Min(Math.Max(540 + 60 * lines, 600), 900));
            var sweep = duration - FrontLead;

            var compositor = BootStrapper.Current.Compositor;
            var easing = compositor.CreateCubicBezierEasingFunction(Ease1, Ease2);

            // Along the normal, from the field's top left corner.
            var start = -Margin;
            var end = width * Normal.X + height * Normal.Y + Margin;

            var front = CreateFront(compositor, start, end, sweep, TimeSpan.Zero, easing);

            _root ??= CreateRoot(compositor);
            _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

            _fieldVisual = ElementComposition.GetElementVisual(field);
            _fieldClip = _fieldVisual.Clip;
            _fieldVisual.Clip = CreateFrontClip(compositor, size, front);

            // The old text keeps the field's old place. In its coordinates the front is further
            // along by however far the field has moved.
            var shift = (origin.X - _origin.X) * Normal.X + (origin.Y - _origin.Y) * Normal.Y;

            _old = CreateOld(compositor, snapshot, _snapshotSize, start + shift, end + shift, sweep, easing);
            _old.Offset = new Vector3(_origin, 0);
            _root.Children.InsertAtTop(_old);

            if (Accent is SolidColorBrush accent)
            {
                _band = CreateBand(compositor, accent.Color, size, start, end, sweep, duration, easing, front);
                _band.Offset = new Vector3(origin, 0);
                _root.Children.InsertAtTop(_band);
            }

            _batch.End();
            _batch.Completed += OnCompleted;

            return duration;
        }

        /// <summary>Takes the transition down where it is, the field whole again.</summary>
        public void Stop()
        {
            Release();
        }

        private void OnCompleted(object sender, CompositionBatchCompletedEventArgs e)
        {
            Release();
        }

        private void Release()
        {
            if (_batch != null)
            {
                _batch.Completed -= OnCompleted;
                _batch = null;
            }

            if (_fieldVisual != null)
            {
                _fieldVisual.Clip = _fieldClip;
                _fieldVisual = null;
                _fieldClip = null;
            }

            if (_old != null)
            {
                _root.Children.Remove(_old);
                _old.Dispose();
                _old = null;
            }

            if (_snapshot != null)
            {
                _snapshot.Dispose();
                _snapshot = null;
            }

            if (_band != null)
            {
                _root.Children.Remove(_band);
                _band.Dispose();
                _band = null;
            }

            if (_bandSurface != null)
            {
                _bandSurface.Dispose();
                _bandSurface = null;
            }

            _field = null;
        }

        // The old text, ahead of the front and fading across the edge as it passes, and its dust.
        private static ContainerVisual CreateOld(Compositor compositor, CompositionVisualSurface snapshot, Vector2 size, float start, float end, TimeSpan sweep, CompositionEasingFunction easing)
        {
            var source = compositor.CreateSurfaceBrush(snapshot);
            source.Stretch = CompositionStretch.Fill;

            var gradient = compositor.CreateLinearGradientBrush();
            gradient.MappingMode = CompositionMappingMode.Absolute;
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(0, 0xFF, 0xFF, 0xFF)));
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
            gradient.StartAnimation("StartPoint", CreateFront(compositor, start - Edge, end - Edge, sweep, TimeSpan.Zero, easing));
            gradient.StartAnimation("EndPoint", CreateFront(compositor, start + Edge, end + Edge, sweep, TimeSpan.Zero, easing));

            var mask = compositor.CreateMaskBrush();
            mask.Source = source;
            mask.Mask = gradient;

            var text = compositor.CreateSpriteVisual();
            text.Brush = mask;
            text.Size = size;

            var old = compositor.CreateContainerVisual();
            old.Children.InsertAtTop(text);

            AddDust(compositor, old, source, size, start, end, sweep);
            return old;
        }

        // Stripes of the old text parallel to the front, each setting off as the front reaches its
        // middle. A stripe is drawn once per dust mask with pixels in it and each copy takes a course
        // of its own: the masks hold every pixel exactly once, so at rest the copies are the stripe.
        private static void AddDust(Compositor compositor, ContainerVisual old, CompositionSurfaceBrush source, Vector2 size, float start, float end, TimeSpan sweep)
        {
            var masks = CompositionDustLayers.Masks;
            if (masks == null)
            {
                return;
            }

            var extent = size.X * Normal.X + size.Y * Normal.Y;
            var count = Math.Min(MaxStripes, Math.Max(1, (int)MathF.Ceiling(extent / StripeWidth)));
            var width = extent / count;

            // Long enough to cross the text whichever way it is turned.
            var length = (size.X + size.Y) * 2;

            var brushes = new CompositionMaskBrush[masks.Length];
            var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.6f), new Vector2(0.2f, 1));

            for (int i = 0; i < count; i++)
            {
                var near = i * width;

                // Where the stripe crosses the text, as fractions of its width, which is what the
                // masks are laid out by.
                var left = Math.Max(0, (near - size.Y * Normal.Y) / Normal.X / size.X);
                var right = Math.Min(1, (near + width) / Normal.X / size.X);

                if (left >= right)
                {
                    continue;
                }

                var first = (int)(left * (1 - CompositionDustLayers.Scatter) * masks.Length);
                var last = Math.Min(masks.Length - 1, (int)((right * (1 - CompositionDustLayers.Scatter) + CompositionDustLayers.Scatter) * masks.Length));

                var delay = TimeSpan.FromMilliseconds(sweep.TotalMilliseconds * FrontTime((near + width / 2 - start) / (end - start)));

                var geometry = compositor.CreateRectangleGeometry();
                geometry.Offset = new Vector2(near, -length);
                geometry.Size = new Vector2(width, length * 2);

                for (int j = first; j <= last; j++)
                {
                    var brush = brushes[j];
                    if (brush == null)
                    {
                        brush = brushes[j] = compositor.CreateMaskBrush();
                        brush.Source = source;
                        brush.Mask = masks[j];
                    }

                    var clip = compositor.CreateGeometricClip(geometry);
                    clip.RotationAngleInDegrees = Angle;

                    // Hidden until it sets off, while the old text still shows these pixels.
                    var layer = compositor.CreateSpriteVisual();
                    layer.Brush = brush;
                    layer.Size = size;
                    layer.Clip = clip;
                    layer.Opacity = 0;

                    var life = TimeSpan.FromMilliseconds(DustLife + _random.NextDouble() * DustLifeRange);

                    var drift = compositor.CreateVector3KeyFrameAnimation();
                    drift.InsertKeyFrame(1, new Vector3(DriftX + (float)_random.NextDouble() * DriftXRange, -(DriftY + (float)_random.NextDouble() * DriftYRange), 0), easing);
                    drift.Duration = life;
                    drift.DelayTime = delay;

                    var fade = compositor.CreateScalarKeyFrameAnimation();
                    fade.InsertKeyFrame(0, 1);
                    fade.InsertKeyFrame(1, 0);
                    fade.Duration = life;
                    fade.DelayTime = delay;

                    layer.StartAnimation("Offset", drift);
                    layer.StartAnimation("Opacity", fade);

                    old.Children.InsertAtTop(layer);
                }
            }
        }

        // The new text in the accent colour behind the front, fading out further back. It is the
        // field's own pixels through a live surface, so it keeps up with what is typed.
        private SpriteVisual CreateBand(Compositor compositor, Color accent, Vector2 size, float start, float end, TimeSpan sweep, TimeSpan duration, CompositionEasingFunction easing, Vector2KeyFrameAnimation front)
        {
            _bandSurface = compositor.CreateVisualSurface();
            _bandSurface.SourceVisual = _fieldVisual;
            _bandSurface.SourceSize = size;

            var text = compositor.CreateSurfaceBrush(_bandSurface);
            text.Stretch = CompositionStretch.Fill;

            // Both ends follow the front's own curve, late by how far behind it they are. The start
            // stays a pixel behind the end, so the stops never meet where both come to rest.
            var gradient = compositor.CreateLinearGradientBrush();
            gradient.MappingMode = CompositionMappingMode.Absolute;
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(0, accent.R, accent.G, accent.B)));
            gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, accent));
            gradient.StartAnimation("StartPoint", CreateFront(compositor, start - 1, end - 1, sweep, BandFade, easing));
            gradient.StartAnimation("EndPoint", CreateFront(compositor, start, end, sweep, BandSolid, easing));

            var brush = compositor.CreateMaskBrush();
            brush.Source = gradient;
            brush.Mask = text;

            var band = compositor.CreateSpriteVisual();
            band.Brush = brush;
            band.Size = size;

            // Clipped too: ahead of the front the gradient is solid, and the surface may or may not
            // carry the field's own clip.
            band.Clip = CreateFrontClip(compositor, size, front);

            // The band's tail is still crossing when the transition ends, so it is faded out rather
            // than left to finish.
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 1);
            fade.InsertKeyFrame((float)(1 - BandFadeOut.TotalMilliseconds / duration.TotalMilliseconds), 1);
            fade.InsertKeyFrame(1, 0);
            fade.Duration = duration;
            band.StartAnimation("Opacity", fade);

            return band;
        }

        // A point moving along the normal on the front's curve, from one distance to the other.
        private static Vector2KeyFrameAnimation CreateFront(Compositor compositor, float from, float to, TimeSpan duration, TimeSpan delay, CompositionEasingFunction easing)
        {
            var animation = compositor.CreateVector2KeyFrameAnimation();
            // Scaled component by component, as CompositionDustVisual explains: the vector spelling
            // came out as zero on .NET Native.
            animation.InsertKeyFrame(0, new Vector2(Normal.X * from, Normal.Y * from));
            animation.InsertKeyFrame(1, new Vector2(Normal.X * to, Normal.Y * to), easing);
            animation.Duration = duration;
            animation.DelayTime = delay;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

            return animation;
        }

        // The fraction of the sweep at which the front has come the given fraction of its way: the
        // curve's x where its y is that fraction, found by bisection since y rises monotonically.
        private static double FrontTime(float progress)
        {
            var target = Math.Min(1, Math.Max(0, progress));

            double low = 0;
            double high = 1;

            for (int i = 0; i < 24; i++)
            {
                var t = (low + high) / 2;

                if (Bezier(t, Ease1.Y, Ease2.Y) < target)
                {
                    low = t;
                }
                else
                {
                    high = t;
                }
            }

            return Bezier((low + high) / 2, Ease1.X, Ease2.X);
        }

        private static double Bezier(double t, double p1, double p2)
        {
            var u = 1 - t;
            return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
        }

        // What the front has passed: a square far larger than the field, its right edge on the
        // front, turned to the front's angle about the field's corner and moved along with it.
        private static CompositionGeometricClip CreateFrontClip(Compositor compositor, Vector2 size, Vector2KeyFrameAnimation front)
        {
            var extent = (size.X + size.Y) * 2;

            var geometry = compositor.CreateRectangleGeometry();
            geometry.Offset = new Vector2(-extent, -extent / 2);
            geometry.Size = new Vector2(extent, extent);

            var clip = compositor.CreateGeometricClip(geometry);
            clip.RotationAngleInDegrees = Angle;
            clip.StartAnimation("Offset", front);

            return clip;
        }

        private ContainerVisual CreateRoot(Compositor compositor)
        {
            var root = compositor.CreateContainerVisual();
            root.RelativeSizeAdjustment = Vector2.One;

            // Dust that drifts out of the room above the field must not paint over the chat.
            root.Clip = compositor.CreateInsetClip();

            ElementComposition.SetElementChildVisual(this, root);
            return root;
        }
    }
}
