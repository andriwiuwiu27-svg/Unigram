//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Numerics;
using System.Threading;
using Telegram.Common;
using Telegram.Navigation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace Telegram.Composition
{
    // Numbered because the setting stores them, and 0 was the in-chat banner, since removed.
    public enum CallIndicatorStyle
    {
        Lens = 1,
        LayeredLens = 2
    }

    public enum LensMotion
    {
        /// <summary>Follows the call's audio.</summary>
        Voice,
        /// <summary>Breathes slowly: a scheduled chat nobody can speak in yet.</summary>
        Calm,
        /// <summary>A bump travelling along the lens, while the call connects.</summary>
        Sweep,
        /// <summary>Still: the lens before it has a call, and every motion with animations off.</summary>
        Rest
    }

    /// <summary>
    /// The waveform of the title bar call indicator: a mirrored lens whose crest flows along it
    /// with the call's audio, optionally with a fainter and livelier lens behind it.
    /// </summary>
    /// <remarks>
    /// Every control point is a damped spring, but nothing runs per frame. A 100 ms timer advances
    /// the springs on the UI thread, samples the shape twice on the way, and hands the samples to
    /// a path key frame animation the compositor interpolates. Ten ticks a second is the rate
    /// tgcalls reports levels at, so sampling faster would only interpolate between levels that
    /// never changed. The timer stops at rest and while the element is unloaded.
    /// </remarks>
    public sealed partial class CompositionLensVisual
    {
        private const int PointCount = 5;

        private const double TickInterval = 0.1;
        private const int SamplesPerTick = 2;

        // The springs integrate at 240 Hz whatever the sampling, so the motion does not depend on
        // how many samples a tick takes.
        private const int SubstepsPerSample = 12;
        private const float Substep = (float)(TickInterval / SamplesPerTick / SubstepsPerSample);

        private const float RestEnergy = 0.22f;
        private const float CalmEnergy = 0.2f;
        private const float VoiceEnergy = 0.35f;

        // The audio level that fills the lens.
        private const float MaxLevel = 1.5f;

        private const double ColorTransitionDuration = 0.167;
        private const double RestTransitionDuration = 0.167;

        private static readonly float[] Weights = CreateWeights();

        private readonly Vector2 _size;
        private readonly Layer[] _layers;

        private readonly ContainerVisual _visual;
        private readonly CompositionColorGradientStop _startStop;
        private readonly CompositionColorGradientStop _endStop;

        private readonly ColorKeyFrameAnimation _colorAnimation;
        private readonly CompositionEasingFunction _linearEasing;
        private readonly CompositionEasingFunction _restEasing;

        private readonly Vector2[] _points = new Vector2[PointCount + 2];
        private readonly float[] _sampled = new float[PointCount];

        private DispatcherTimer _timer;

        private LensMotion _motion = LensMotion.Rest;
        private bool _started;

        // Written by the call's audio callback, off the UI thread.
        private float _level;

        private float _energy;
        private float _energyVelocity;
        private float _phase;

        private Color _startColor;
        private Color _endColor;

        public CompositionLensVisual(UIElement element, Vector2 size, bool layered)
        {
            var compositor = BootStrapper.Current.Compositor;

            _size = size;

            _startStop = compositor.CreateColorGradientStop(0, default);
            _endStop = compositor.CreateColorGradientStop(1, default);

            _linearEasing = compositor.CreateLinearEasingFunction();
            _restEasing = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));

            _colorAnimation = compositor.CreateColorKeyFrameAnimation();
            _colorAnimation.InterpolationColorSpace = CompositionColorSpace.Rgb;
            _colorAnimation.Duration = TimeSpan.FromSeconds(ColorTransitionDuration);

            var gradient = compositor.CreateLinearGradientBrush();
            gradient.ColorStops.Add(_startStop);
            gradient.ColorStops.Add(_endStop);

            // The gradient runs over twice the lens, so only the first half of each colour pair
            // shows: the pairs are picked for that half.
            gradient.EndPoint = new Vector2(2, 0);

            _visual = compositor.CreateContainerVisual();
            _visual.Size = size;

            // The back layer is the larger, fainter and livelier one.
            _layers = layered
                ? new[] { new Layer(compositor, size, gradient, 1, 0.32f, 2.1f, 0.8f), new Layer(compositor, size, gradient, 0.68f, 1, 0, 1) }
                : new[] { new Layer(compositor, size, gradient, 1, 1, 0, 1) };

            foreach (var layer in _layers)
            {
                layer.SetRest(_phase);
                layer.Current = CreatePath(layer.X, layer.Amplitude);
                layer.Geometry.Path = layer.Current;

                _visual.Children.InsertAtTop(layer.Visual);
            }

            ElementCompositionPreview.SetElementChildVisual(element, _visual);
        }

        private static bool CanAnimate => ApiInfo.CanAnimatePaths && PowerSavingPolicy.AreMaterialsEnabled && PowerSavingPolicy.AreSmoothTransitionsEnabled;

        /// <summary>
        /// May be called from any thread.
        /// </summary>
        public void UpdateLevel(float level)
        {
            Volatile.Write(ref _level, MathF.Min(1, MathF.Max(level / MaxLevel, 0)));
        }

        public void SetColors(Color start, Color end, bool animated)
        {
            if (start == _startColor && end == _endColor)
            {
                return;
            }

            _startColor = start;
            _endColor = end;

            SetStopColor(_startStop, start, animated);
            SetStopColor(_endStop, end, animated);
        }

        private void SetStopColor(CompositionColorGradientStop stop, Color color, bool animated)
        {
            if (animated)
            {
                // No key frame at 0, so an interrupted transition continues from what is on screen.
                _colorAnimation.InsertKeyFrame(1, color, _linearEasing);
                stop.StartAnimation("Color", _colorAnimation);
            }
            else
            {
                // A write does not override a running animation, which would land on its old target.
                stop.StopAnimation("Color");
                stop.Color = color;
            }
        }

        public LensMotion Motion
        {
            get => _motion;
            set
            {
                if (_motion != value)
                {
                    _motion = value;
                    Update();
                }
            }
        }

        public void Start()
        {
            _started = true;
            Update();
        }

        public void Stop()
        {
            _started = false;
            Update();
        }

        private void Update()
        {
            if (_started && _motion != LensMotion.Rest && CanAnimate)
            {
                if (_timer == null)
                {
                    _timer = new DispatcherTimer();
                    _timer.Interval = TimeSpan.FromSeconds(TickInterval);
                    _timer.Tick += OnTick;
                }

                if (!_timer.IsEnabled)
                {
                    _timer.Start();
                    OnTick(_timer, null);
                }
            }
            else
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTick;
                    _timer = null;
                }

                GoToRest(_started && CanAnimate);
            }
        }

        private void GoToRest(bool animated)
        {
            _energy = RestEnergy;
            _energyVelocity = 0;

            foreach (var layer in _layers)
            {
                layer.SetRest(_phase);

                var path = CreatePath(layer.X, layer.Amplitude);

                if (animated)
                {
                    layer.RestAnimation.InsertKeyFrame(0, layer.Current, _linearEasing);
                    layer.RestAnimation.InsertKeyFrame(1, path, _restEasing);
                    layer.RestAnimation.Duration = TimeSpan.FromSeconds(RestTransitionDuration);
                    layer.Geometry.StartAnimation("Path", layer.RestAnimation);
                }
                else
                {
                    layer.Geometry.StopAnimation("Path");
                    layer.Geometry.Path = path;
                }

                layer.Current = path;
            }
        }

        private void OnTick(object sender, object e)
        {
            var level = Volatile.Read(ref _level);

            foreach (var layer in _layers)
            {
                layer.Animation.InsertKeyFrame(0, layer.Current, _linearEasing);
            }

            for (int sample = 1; sample <= SamplesPerTick; sample++)
            {
                for (int step = 0; step < SubstepsPerSample; step++)
                {
                    Advance(level);
                }

                var progress = (float)sample / SamplesPerTick;

                foreach (var layer in _layers)
                {
                    var path = CreatePath(SampleHeights(layer), layer.Amplitude);
                    layer.Animation.InsertKeyFrame(progress, path, _linearEasing);
                    layer.Current = path;
                }
            }

            foreach (var layer in _layers)
            {
                layer.Animation.Duration = TimeSpan.FromSeconds(TickInterval);
                layer.Geometry.StartAnimation("Path", layer.Animation);
            }
        }

        private void Advance(float level)
        {
            // The square root lifts speech, which tgcalls reports well under the maximum, and the
            // floor keeps a silent call moving: the motion scales with the energy, so at zero the
            // lens is a still line that reads as a frozen indicator.
            var target = _motion switch
            {
                LensMotion.Voice => VoiceEnergy + (1 - VoiceEnergy) * MathF.Sqrt(level),
                LensMotion.Calm => CalmEnergy,
                LensMotion.Sweep => 0.4f,
                _ => RestEnergy
            };

            // Rises quickly and lets go a little more slowly, with a touch of overshoot both ways.
            var rising = target > _energy;
            Spring(ref _energy, ref _energyVelocity, target, rising ? 260 : 120, rising ? 20 : 15);

            var speed = _motion switch
            {
                LensMotion.Voice => 1 + 1.2f * Clamp01(_energy),
                LensMotion.Calm => 0.3f,
                _ => 1
            };

            _phase += Substep * speed;

            if (_motion == LensMotion.Sweep)
            {
                return;
            }

            foreach (var layer in _layers)
            {
                var phase = (_phase * layer.Speed + layer.PhaseOffset) * 5.3f;

                for (int i = 0; i < PointCount; i++)
                {
                    // Neighbouring points move nearly in phase, so the crest travels instead of spiking.
                    var t = _energy * Weights[i] * (0.6f + 0.4f * MathF.Sin(phase - i * 0.75f));
                    Spring(ref layer.X[i], ref layer.V[i], t, 220, 17);
                }
            }
        }

        private float[] SampleHeights(Layer layer)
        {
            if (_motion != LensMotion.Sweep)
            {
                return layer.X;
            }

            var center = (_phase * layer.Speed * 0.4f + layer.PhaseOffset * 0.1f) % 1.4f - 0.2f;

            for (int i = 0; i < PointCount; i++)
            {
                var u = (i + 1f) / (PointCount + 1f) - center;
                _sampled[i] = 0.6f * MathF.Exp(-u * u / 0.02f);
            }

            return _sampled;
        }

        // Damping below critical is what gives the overshoot and settle that reads as physical.
        private static void Spring(ref float x, ref float v, float target, float stiffness, float damping)
        {
            v += (stiffness * (target - x) - damping * v) * Substep;
            x += v * Substep;
        }

        private CompositionPath CreatePath(float[] heights, float amplitude)
        {
            var cy = _size.Y / 2;
            var maxHeight = cy - 0.6f;
            var last = PointCount + 1;

            _points[0] = new Vector2(0.8f, cy);
            _points[last] = new Vector2(_size.X - 0.8f, cy);

            for (int i = 0; i < PointCount; i++)
            {
                var u = (i + 1f) / (PointCount + 1f);
                _points[i + 1] = new Vector2(0.8f + (_size.X - 1.6f) * u, cy - MathF.Max(0.55f, Clamp01(heights[i] * amplitude) * maxHeight));
            }

            CanvasGeometry geometry;
            using (var builder = new CanvasPathBuilder(null))
            {
                builder.BeginFigure(_points[0]);

                // Catmull-Rom through the top edge, then back along its mirror image. The segment
                // count never changes, which a path key frame animation requires.
                for (int i = 0; i < last; i++)
                {
                    AddSegment(builder, _points[Math.Max(0, i - 1)], _points[i], _points[i + 1], _points[Math.Min(last, i + 2)]);
                }

                for (int i = last; i > 0; i--)
                {
                    AddSegment(builder, Mirror(_points[Math.Min(last, i + 1)], cy), Mirror(_points[i], cy), Mirror(_points[i - 1], cy), Mirror(_points[Math.Max(0, i - 2)], cy));
                }

                builder.EndFigure(CanvasFigureLoop.Closed);
                geometry = CanvasGeometry.CreatePath(builder);
            }

            using (geometry)
            {
                return new CompositionPath(geometry);
            }
        }

        private static void AddSegment(CanvasPathBuilder builder, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            builder.AddCubicBezier(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2);
        }

        private static Vector2 Mirror(Vector2 point, float cy)
        {
            return new Vector2(point.X, 2 * cy - point.Y);
        }

        private static float Clamp01(float value)
        {
            return value < 0 ? 0 : value > 1 ? 1 : value;
        }

        private static float[] CreateWeights()
        {
            var weights = new float[PointCount];

            for (int i = 0; i < PointCount; i++)
            {
                weights[i] = MathF.Pow(MathF.Sin(MathF.PI * (i + 1) / (PointCount + 1)), 1.6f);
            }

            return weights;
        }

        private sealed class Layer
        {
            public readonly ShapeVisual Visual;
            public readonly CompositionPathGeometry Geometry;
            public readonly PathKeyFrameAnimation Animation;

            // Its own animation, because the tick's has a frame halfway that cannot be removed.
            public readonly PathKeyFrameAnimation RestAnimation;

            public readonly float Amplitude;
            public readonly float PhaseOffset;
            public readonly float Speed;

            public readonly float[] X = new float[PointCount];
            public readonly float[] V = new float[PointCount];

            // Where the last animation ends, for the next to start from: a path read back from a
            // running animation is neither of the paths it was given.
            public CompositionPath Current;

            public Layer(Compositor compositor, Vector2 size, CompositionBrush brush, float amplitude, float opacity, float phaseOffset, float speed)
            {
                Amplitude = amplitude;
                PhaseOffset = phaseOffset;
                Speed = speed;

                Geometry = compositor.CreatePathGeometry();
                Animation = compositor.CreatePathKeyFrameAnimation();
                RestAnimation = compositor.CreatePathKeyFrameAnimation();

                var shape = compositor.CreateSpriteShape(Geometry);
                shape.FillBrush = brush;

                Visual = compositor.CreateShapeVisual();
                Visual.Size = size;
                Visual.Opacity = opacity;
                Visual.Shapes.Add(shape);
            }

            public void SetRest(float phase)
            {
                var p = (phase * Speed + PhaseOffset) * 5.3f;

                for (int i = 0; i < PointCount; i++)
                {
                    X[i] = RestEnergy * Weights[i] * (0.6f + 0.4f * MathF.Sin(p - i * 0.75f));
                    V[i] = 0;
                }
            }
        }
    }
}
