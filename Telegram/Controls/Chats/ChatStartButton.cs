//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Controls.Chats
{
    /// <summary>
    /// The accent pill that starts a bot, with a highlight sweeping across it while it is visible.
    /// </summary>
    public partial class ChatStartButton : Button
    {
        // The band is 160 wide and a sweep takes 1.2 s, followed by as long a pause.
        private const float BandWidth = 160;
        private static readonly TimeSpan SweepDuration = TimeSpan.FromMilliseconds(2400);

        private FrameworkElement ShimmerHost;

        private SpriteVisual _band;
        private CompositionRoundedRectangleGeometry _clip;
        private Vector2 _size;
        private bool _loaded;

        public ChatStartButton()
        {
            DefaultStyleKey = typeof(ChatStartButton);

            // A looping animation keeps the compositor busy even under a collapsed element, and the
            // page hosting this one can leave the tree with the button still visible.
            RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            UpdateSweep();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            UpdateSweep();
        }

        protected override void OnApplyTemplate()
        {
            if (ShimmerHost != null)
            {
                ShimmerHost.SizeChanged -= OnSizeChanged;
            }

            ShimmerHost = GetTemplateChild(nameof(ShimmerHost)) as FrameworkElement;

            if (ShimmerHost != null)
            {
                ShimmerHost.SizeChanged += OnSizeChanged;
            }

            _band = null;
            _clip = null;

            base.OnApplyTemplate();
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
        {
            UpdateSweep();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            UpdateSweep();
        }

        private void UpdateSweep()
        {
            if (ShimmerHost == null || _size.X <= 0 || _size.Y <= 0)
            {
                return;
            }

            if (_band == null)
            {
                var compositor = ElementComposition.GetElementVisual(ShimmerHost).Compositor;

                var gradient = compositor.CreateLinearGradientBrush();
                gradient.StartPoint = new Vector2(0, 0);
                gradient.EndPoint = new Vector2(1, 0);
                gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)));
                gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
                gradient.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)));

                _band = compositor.CreateSpriteVisual();
                _band.Brush = gradient;

                _clip = compositor.CreateRoundedRectangleGeometry();

                var container = compositor.CreateContainerVisual();
                container.RelativeSizeAdjustment = Vector2.One;
                container.Clip = compositor.CreateGeometricClip(_clip);
                container.Children.InsertAtTop(_band);

                ElementComposition.SetElementChildVisual(ShimmerHost, container);
            }

            var radius = (float)Math.Min(CornerRadius.TopLeft, _size.Y / 2);

            _clip.Size = _size;
            _clip.CornerRadius = new Vector2(radius);
            _band.Size = new Vector2(BandWidth, _size.Y);

            if (Visibility == Visibility.Collapsed || !_loaded)
            {
                _band.StopAnimation("Offset");
                return;
            }

            var animation = _band.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0, new Vector3(-BandWidth, 0, 0), _band.Compositor.CreateLinearEasingFunction());
            animation.InsertKeyFrame(0.5f, new Vector3(_size.X + BandWidth, 0, 0), _band.Compositor.CreateLinearEasingFunction());
            animation.InsertKeyFrame(1, new Vector3(_size.X + BandWidth, 0, 0));
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            animation.Duration = SweepDuration;

            _band.StartAnimation("Offset", animation);
        }
    }
}
