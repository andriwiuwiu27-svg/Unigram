//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System;
using System.Collections.Generic;
using System.Numerics;
using Telegram.Navigation;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Telegram.Common
{
    /// <summary>
    /// A brush shaped like an element, for the mask of a drop shadow or of a mask brush, drawn from
    /// vector shapes in a visual surface. The brush stretches the mask's box over whatever it
    /// paints, as an alpha mask's did.
    /// </summary>
    /// <remarks>
    /// Never <c>GetAlphaMask</c>. Once a shape, image or text block has handed out an alpha mask,
    /// XAML rasterizes the element into a surface of its own each time the render walk renders it,
    /// and that path fail-fasts the process when the device is removed or D2D asks for its target
    /// to be recreated. Shapes are vector content that composition draws itself: nothing is
    /// rasterized in the walk and nothing is lost with the device.
    /// </remarks>
    public sealed partial class OutlineMask
    {
        private readonly ShapeVisual _visual;
        private readonly CompositionVisualSurface _surface;

        private CompositionColorBrush _fill;

        public OutlineMask(Compositor compositor)
        {
            _visual = compositor.CreateShapeVisual();

            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = _visual;

            Brush = compositor.CreateSurfaceBrush(_surface);
        }

        public CompositionSurfaceBrush Brush { get; }

        /// <summary>
        /// The box the brush covers, from the shapes' origin. Anything outside it is cut off, so a
        /// shape that reaches into negative coordinates is moved in with its offset.
        /// </summary>
        public Vector2 Size
        {
            get => _visual.Size;
            set
            {
                _visual.Size = value;
                _surface.SourceSize = value;
            }
        }

        public CompositionSpriteShape Add(CompositionGeometry geometry, float strokeThickness = 0)
        {
            var compositor = _visual.Compositor;
            _fill ??= compositor.CreateColorBrush(Colors.White);

            var shape = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = _fill;
            shape.StrokeThickness = strokeThickness;

            if (strokeThickness > 0)
            {
                shape.StrokeBrush = _fill;
            }

            _visual.Shapes.Add(shape);
            return shape;
        }

        /// <summary>
        /// A rectangle's rounded outline, which keeps to the size XAML lays it out at: a rectangle
        /// casting a shadow is usually stretched over a button whose label decides its width.
        /// </summary>
        public static CompositionSurfaceBrush FromRectangle(Rectangle rectangle)
        {
            var host = ElementComposition.GetElementVisual(rectangle);
            var compositor = host.Compositor;

            var mask = new OutlineMask(compositor);

            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2((float)rectangle.RadiusX, (float)rectangle.RadiusY);
            mask.Add(geometry);

            var size = compositor.CreateExpressionAnimation("host.Size");
            size.SetReferenceParameter("host", host);

            geometry.StartAnimation("Size", size);
            mask._visual.StartAnimation("Size", size);
            mask._surface.StartAnimation("SourceSize", size);

            return mask.Brush;
        }

        public static CompositionSurfaceBrush FromEllipse(Vector2 size)
        {
            var mask = new OutlineMask(BootStrapper.Current.Compositor);

            var geometry = mask._visual.Compositor.CreateEllipseGeometry();
            geometry.Center = size / 2;
            geometry.Radius = size / 2;

            mask.Add(geometry);
            mask.Size = size;

            return mask.Brush;
        }

        /// <summary>
        /// A path's fill and stroke as it draws them, with no stretch: its own geometry in a box of
        /// <paramref name="size"/>, which is its layout size.
        /// </summary>
        /// <remarks>
        /// Read once, so a path whose data changes needs a new mask. Groups, path figures of line,
        /// arc and Bézier segments, ellipses and rectangles are read; transforms are not.
        /// </remarks>
        public static CompositionSurfaceBrush FromPath(XamlPath path, Vector2 size)
        {
            var mask = new OutlineMask(BootStrapper.Current.Compositor);

            // Rooted with using: Win2D's UWP projection has no GC.KeepAlive, so a geometry whose
            // only reference is a temporary can be finalized while CompositionPath is reading it.
            using var geometry = ToCanvasGeometry(ElementComposition.GetSharedDevice(), path.Data);
            if (geometry != null)
            {
                var stroke = path.Stroke != null ? (float)path.StrokeThickness : 0;
                mask.Add(mask._visual.Compositor.CreatePathGeometry(new CompositionPath(geometry)), stroke);
            }

            mask.Size = size;
            return mask.Brush;
        }

        private static CanvasGeometry ToCanvasGeometry(CanvasDevice device, Geometry geometry)
        {
            switch (geometry)
            {
                case GeometryGroup group:
                    var children = new List<CanvasGeometry>(group.Children.Count);

                    for (int i = 0; i < group.Children.Count; i++)
                    {
                        var child = ToCanvasGeometry(device, group.Children[i]);
                        if (child != null)
                        {
                            children.Add(child);
                        }
                    }

                    var result = CanvasGeometry.CreateGroup(device, children.ToArray(), ToFill(group.FillRule));

                    // The group holds the geometries themselves, not the wrappers.
                    foreach (var child in children)
                    {
                        child.Dispose();
                    }

                    return result;
                case PathGeometry path:
                    return ToCanvasGeometry(device, path);
                case EllipseGeometry ellipse:
                    return CanvasGeometry.CreateEllipse(device, ToVector2(ellipse.Center), (float)ellipse.RadiusX, (float)ellipse.RadiusY);
                case RectangleGeometry rectangle:
                    var rect = rectangle.Rect;
                    return CanvasGeometry.CreateRectangle(device, (float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
            }

            return null;
        }

        private static CanvasGeometry ToCanvasGeometry(CanvasDevice device, PathGeometry path)
        {
            using var builder = new CanvasPathBuilder(device);
            builder.SetFilledRegionDetermination(ToFill(path.FillRule));

            for (int i = 0; i < path.Figures.Count; i++)
            {
                var figure = path.Figures[i];
                builder.BeginFigure(ToVector2(figure.StartPoint), figure.IsFilled ? CanvasFigureFill.Default : CanvasFigureFill.DoesNotAffectFills);

                for (int j = 0; j < figure.Segments.Count; j++)
                {
                    switch (figure.Segments[j])
                    {
                        case LineSegment line:
                            builder.AddLine(ToVector2(line.Point));
                            break;
                        case PolyLineSegment polyLine:
                            for (int k = 0; k < polyLine.Points.Count; k++)
                            {
                                builder.AddLine(ToVector2(polyLine.Points[k]));
                            }
                            break;
                        case BezierSegment bezier:
                            builder.AddCubicBezier(ToVector2(bezier.Point1), ToVector2(bezier.Point2), ToVector2(bezier.Point3));
                            break;
                        case QuadraticBezierSegment quadratic:
                            builder.AddQuadraticBezier(ToVector2(quadratic.Point1), ToVector2(quadratic.Point2));
                            break;
                        case ArcSegment arc:
                            // XAML's angle is in degrees, Win2D's in radians.
                            builder.AddArc(ToVector2(arc.Point), (float)arc.Size.Width, (float)arc.Size.Height, (float)(arc.RotationAngle * Math.PI / 180),
                                arc.SweepDirection == SweepDirection.Clockwise ? CanvasSweepDirection.Clockwise : CanvasSweepDirection.CounterClockwise,
                                arc.IsLargeArc ? CanvasArcSize.Large : CanvasArcSize.Small);
                            break;
                    }
                }

                builder.EndFigure(figure.IsClosed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
            }

            return CanvasGeometry.CreatePath(builder);
        }

        private static CanvasFilledRegionDetermination ToFill(FillRule rule)
        {
            return rule == FillRule.Nonzero ? CanvasFilledRegionDetermination.Winding : CanvasFilledRegionDetermination.Alternate;
        }

        private static Vector2 ToVector2(Point point)
        {
            return new Vector2((float)point.X, (float)point.Y);
        }
    }
}
