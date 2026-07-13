using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VisionStation.Vision.UI.Models;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Controls;

public sealed class ResultOverlayCanvas : FrameworkElement
{
    public static readonly DependencyProperty ImageFrameProperty = DependencyProperty.Register(
        nameof(ImageFrame),
        typeof(ImageFrame),
        typeof(ResultOverlayCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(ResultOverlayCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    private readonly Brush _labelBrush = new SolidColorBrush(Color.FromArgb(220, 5, 11, 19));
    private readonly Brush _axisBrush = new SolidColorBrush(Color.FromRgb(0, 230, 70));
    private Rect _imageViewport;
    private double _scale = 1;
    private double _renderScale = 1;

    public ImageFrame? ImageFrame
    {
        get => (ImageFrame?)GetValue(ImageFrameProperty);
        set => SetValue(ImageFrameProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        UpdateViewport();
        _renderScale = GetAncestorRenderScale();

        foreach (var item in EnumerateItems())
        {
            DrawItem(drawingContext, item);
        }
    }

    private void DrawItem(DrawingContext drawingContext, VisionOverlayItem item)
    {
        var pen = CreatePen(item.State);
        var fill = CreateFill(item.State);

        switch (item.Kind)
        {
            case VisionOverlayKind.Circle:
                drawingContext.DrawEllipse(fill, pen, ToScreenPoint(new Point(item.X, item.Y)), item.Radius * _scale, item.Radius * _scale);
                DrawLabel(drawingContext, item.Label, new Point(item.X - item.Radius, item.Y - item.Radius));
                break;
            case VisionOverlayKind.CircleAnnulus:
                DrawCircleAnnulus(drawingContext, item, pen);
                DrawLabel(drawingContext, item.Label, new Point(item.X - item.Radius, item.Y - item.Radius));
                break;
            case VisionOverlayKind.RotatedRectangle:
                DrawRotatedRectangle(drawingContext, item, pen, fill);
                DrawLabel(drawingContext, item.Label, GetRotatedBounds(item).TopLeft);
                break;
            case VisionOverlayKind.RotatedRectangleOutline:
                DrawRotatedRectangle(drawingContext, item, pen, null);
                DrawLabel(drawingContext, item.Label, GetRotatedBounds(item).TopLeft);
                break;
            case VisionOverlayKind.Polygon:
                DrawPolygon(drawingContext, item, pen, fill);
                if (item.Points.Count > 0)
                {
                    DrawLabel(drawingContext, item.Label, new Point(item.Points.Min(point => point.X), item.Points.Min(point => point.Y)));
                }
                break;
            case VisionOverlayKind.Polyline:
                DrawPolyline(drawingContext, item, pen);
                if (item.Points.Count > 0)
                {
                    DrawLabel(drawingContext, item.Label, new Point(item.Points.Min(point => point.X), item.Points.Min(point => point.Y)));
                }
                break;
            case VisionOverlayKind.PointCloud:
                DrawPointCloud(drawingContext, item, CreateBrush(item.State));
                break;
            case VisionOverlayKind.XMarker:
                DrawXMarkers(drawingContext, item);
                break;
            case VisionOverlayKind.Cross:
                DrawCross(drawingContext, item, pen);
                DrawLabel(drawingContext, item.Label, new Point(item.X + 10, item.Y + 10));
                break;
            case VisionOverlayKind.LineSegment:
                DrawLineSegment(drawingContext, item, pen);
                DrawLabel(drawingContext, item.Label, MidPoint(new Point(item.X, item.Y), new Point(item.X2, item.Y2)));
                break;
            case VisionOverlayKind.Line:
                DrawLine(drawingContext, item, pen);
                DrawLabel(drawingContext, item.Label, MidPoint(new Point(item.X, item.Y), new Point(item.X2, item.Y2)));
                break;
            case VisionOverlayKind.DirectionAxis:
                DrawAxis(drawingContext, new Point(item.X, item.Y), new Point(item.X2, item.Y2), CreateAxisPen());
                DrawLabel(drawingContext, item.Label, new Point(item.X2 + 8, item.Y2 + 8));
                break;
            default:
                DrawRectangle(drawingContext, item, pen, fill);
                DrawLabel(drawingContext, item.Label, new Point(item.X, item.Y));
                break;
        }
    }

    private void DrawRectangle(DrawingContext drawingContext, VisionOverlayItem item, Pen pen, Brush fill)
    {
        var topLeft = ToScreenPoint(new Point(item.X, item.Y));
        drawingContext.DrawRectangle(fill, pen, new Rect(topLeft.X, topLeft.Y, item.Width * _scale, item.Height * _scale));
    }

    private void DrawCircleAnnulus(DrawingContext drawingContext, VisionOverlayItem item, Pen pen)
    {
        var center = ToScreenPoint(new Point(item.X, item.Y));
        var innerRadius = Math.Max(0, item.Width);
        var outerRadius = Math.Max(innerRadius, item.Radius);
        drawingContext.DrawEllipse(null, pen, center, outerRadius * _scale, outerRadius * _scale);
        drawingContext.DrawEllipse(null, pen, center, innerRadius * _scale, innerRadius * _scale);
    }

    private void DrawRotatedRectangle(DrawingContext drawingContext, VisionOverlayItem item, Pen pen, Brush? fill)
    {
        var center = ToScreenPoint(new Point(item.X, item.Y));
        var rect = new Rect(center.X - item.Width * _scale / 2, center.Y - item.Height * _scale / 2, item.Width * _scale, item.Height * _scale);
        drawingContext.PushTransform(new RotateTransform(item.Angle, center.X, center.Y));
        drawingContext.DrawRectangle(fill, pen, rect);
        drawingContext.Pop();
    }

    private void DrawPolygon(DrawingContext drawingContext, VisionOverlayItem item, Pen pen, Brush fill)
    {
        if (item.Points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = ToScreenPoint(new Point(item.Points[0].X, item.Points[0].Y));
            context.BeginFigure(first, item.Points.Count > 2, item.Points.Count > 2);
            context.PolyLineTo(item.Points.Skip(1).Select(point => ToScreenPoint(new Point(point.X, point.Y))).ToArray(), true, true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(item.Points.Count > 2 ? fill : null, pen, geometry);
    }

    private void DrawPolyline(DrawingContext drawingContext, VisionOverlayItem item, Pen pen)
    {
        if (item.Points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = ToScreenPoint(new Point(item.Points[0].X, item.Points[0].Y));
            context.BeginFigure(first, false, item.Points.Count > 2);
            context.PolyLineTo(item.Points.Skip(1).Select(point => ToScreenPoint(new Point(point.X, point.Y))).ToArray(), true, true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private void DrawPointCloud(DrawingContext drawingContext, VisionOverlayItem item, Brush brush)
    {
        var pointSize = ToLocalLength(2.1);
        foreach (var point in item.Points)
        {
            var screenPoint = ToScreenPoint(new Point(point.X, point.Y));
            drawingContext.DrawRectangle(
                brush,
                null,
                new Rect(screenPoint.X - pointSize / 2, screenPoint.Y - pointSize / 2, pointSize, pointSize));
        }
    }

    private void DrawXMarkers(DrawingContext drawingContext, VisionOverlayItem item)
    {
        if (item.Points.Count == 0)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(150, 68, 235, 212)), ToLocalLength(1.2));
        var size = ToLocalLength(5.5);
        foreach (var point in item.Points)
        {
            var screenPoint = ToScreenPoint(new Point(point.X, point.Y));
            drawingContext.DrawLine(pen, new Point(screenPoint.X - size, screenPoint.Y - size), new Point(screenPoint.X + size, screenPoint.Y + size));
            drawingContext.DrawLine(pen, new Point(screenPoint.X - size, screenPoint.Y + size), new Point(screenPoint.X + size, screenPoint.Y - size));
        }
    }

    private void DrawCross(DrawingContext drawingContext, VisionOverlayItem item, Pen pen)
    {
        var center = ToScreenPoint(new Point(item.X, item.Y));
        var size = ToLocalLength(12);
        drawingContext.DrawLine(pen, new Point(center.X - size, center.Y), new Point(center.X + size, center.Y));
        drawingContext.DrawLine(pen, new Point(center.X, center.Y - size), new Point(center.X, center.Y + size));
    }

    private void DrawLine(DrawingContext drawingContext, VisionOverlayItem item, Pen pen)
    {
        DrawLineSegment(drawingContext, item, pen);
        DrawEndpoint(drawingContext, new Point(item.X, item.Y), pen.Brush);
        DrawEndpoint(drawingContext, new Point(item.X2, item.Y2), pen.Brush);
    }

    private void DrawLineSegment(DrawingContext drawingContext, VisionOverlayItem item, Pen pen)
    {
        drawingContext.DrawLine(pen, ToScreenPoint(new Point(item.X, item.Y)), ToScreenPoint(new Point(item.X2, item.Y2)));
    }

    private void DrawAxis(DrawingContext drawingContext, Point start, Point end, Pen pen)
    {
        drawingContext.DrawLine(pen, ToScreenPoint(start), ToScreenPoint(end));

        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var length = 14 / Math.Max(_scale * _renderScale, 0.001);
        var spread = Math.PI / 7;
        var p1 = new Point(end.X - length * Math.Cos(angle - spread), end.Y - length * Math.Sin(angle - spread));
        var p2 = new Point(end.X - length * Math.Cos(angle + spread), end.Y - length * Math.Sin(angle + spread));
        drawingContext.DrawLine(pen, ToScreenPoint(end), ToScreenPoint(p1));
        drawingContext.DrawLine(pen, ToScreenPoint(end), ToScreenPoint(p2));
    }

    private void DrawRectangle2Direction(DrawingContext drawingContext, VisionOverlayItem item)
    {
        var center = new Point(item.X, item.Y);
        var end = RotateLocal(new Point(item.Width / 2, 0), center, item.Angle);
        DrawAxis(drawingContext, center, end, CreateAxisPen());
    }

    private void DrawEndpoint(DrawingContext drawingContext, Point imagePoint, Brush brush)
    {
        var point = ToScreenPoint(imagePoint);
        var halfSize = ToLocalLength(3.5);
        drawingContext.DrawRectangle(brush, null, new Rect(point.X - halfSize, point.Y - halfSize, halfSize * 2, halfSize * 2));
    }

    private void DrawLabel(DrawingContext drawingContext, string label, Point imagePoint)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var screenPoint = ToScreenPoint(imagePoint);
        var text = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            ToLocalLength(12),
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var labelWidth = text.Width + ToLocalLength(14);
        var labelHeight = ToLocalLength(21);
        var left = Math.Clamp(screenPoint.X, 0, Math.Max(0, ActualWidth - labelWidth));
        var top = Math.Clamp(screenPoint.Y - ToLocalLength(24), 0, Math.Max(0, ActualHeight - labelHeight));
        var labelRect = new Rect(left, top, labelWidth, labelHeight);
        drawingContext.DrawRoundedRectangle(_labelBrush, null, labelRect, ToLocalLength(4), ToLocalLength(4));
        drawingContext.DrawText(text, new Point(labelRect.Left + ToLocalLength(7), labelRect.Top + ToLocalLength(2.5)));
    }

    private Pen CreatePen(VisionOverlayState state)
    {
        var color = state switch
        {
            VisionOverlayState.Ok => Color.FromRgb(66, 229, 142),
            VisionOverlayState.Ng => Color.FromRgb(255, 92, 122),
            VisionOverlayState.Warning => Color.FromRgb(255, 200, 87),
            VisionOverlayState.Info => Color.FromRgb(35, 211, 245),
            _ => Color.FromRgb(35, 211, 245)
        };

        return new Pen(new SolidColorBrush(color), ToLocalLength(state == VisionOverlayState.Neutral ? 1.05 : 1.35));
    }

    private Pen CreateAxisPen()
    {
        return new Pen(_axisBrush, ToLocalLength(1.25));
    }

    private Brush CreateFill(VisionOverlayState state)
    {
        var color = state switch
        {
            VisionOverlayState.Ok => Color.FromArgb(18, 66, 229, 142),
            VisionOverlayState.Ng => Color.FromArgb(20, 255, 92, 122),
            VisionOverlayState.Warning => Color.FromArgb(18, 255, 200, 87),
            VisionOverlayState.Info => Color.FromArgb(18, 35, 211, 245),
            _ => Color.FromArgb(14, 35, 211, 245)
        };

        return new SolidColorBrush(color);
    }

    private static Brush CreateBrush(VisionOverlayState state)
    {
        var color = state switch
        {
            VisionOverlayState.Ok => Color.FromArgb(220, 66, 229, 142),
            VisionOverlayState.Ng => Color.FromArgb(230, 255, 92, 122),
            VisionOverlayState.Warning => Color.FromArgb(220, 255, 200, 87),
            VisionOverlayState.Info => Color.FromArgb(220, 35, 211, 245),
            _ => Color.FromArgb(220, 35, 211, 245)
        };

        return new SolidColorBrush(color);
    }

    private Rect GetRotatedBounds(VisionOverlayItem item)
    {
        var center = new Point(item.X, item.Y);
        var halfWidth = item.Width / 2;
        var halfHeight = item.Height / 2;
        var corners = new[]
        {
            RotateLocal(new Point(-halfWidth, -halfHeight), center, item.Angle),
            RotateLocal(new Point(halfWidth, -halfHeight), center, item.Angle),
            RotateLocal(new Point(halfWidth, halfHeight), center, item.Angle),
            RotateLocal(new Point(-halfWidth, halfHeight), center, item.Angle)
        };

        return new Rect(
            new Point(corners.Min(point => point.X), corners.Min(point => point.Y)),
            new Point(corners.Max(point => point.X), corners.Max(point => point.Y)));
    }

    private void UpdateViewport()
    {
        var frame = ImageFrame;
        if (frame is null || frame.Width <= 0 || frame.Height <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _scale = 1;
            _imageViewport = new Rect(0, 0, ActualWidth, ActualHeight);
            return;
        }

        _scale = Math.Min(ActualWidth / frame.Width, ActualHeight / frame.Height);
        var width = frame.Width * _scale;
        var height = frame.Height * _scale;
        _imageViewport = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    private Point ToScreenPoint(Point imagePoint)
    {
        return new Point(_imageViewport.Left + imagePoint.X * _scale, _imageViewport.Top + imagePoint.Y * _scale);
    }

    private double ToLocalLength(double screenLength)
    {
        return screenLength / Math.Max(_renderScale, 0.001);
    }

    private double GetAncestorRenderScale()
    {
        var scale = 1.0;
        DependencyObject? current = this;
        while ((current = VisualTreeHelper.GetParent(current)) is not null)
        {
            if (current is not UIElement element)
            {
                continue;
            }

            var matrix = element.RenderTransform.Value;
            if (matrix.IsIdentity)
            {
                continue;
            }

            var xScale = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
            var yScale = Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22);
            var elementScale = Math.Max(xScale, yScale);
            if (elementScale > 0 && double.IsFinite(elementScale))
            {
                scale *= elementScale;
            }
        }

        return Math.Max(scale, 0.001);
    }

    private static Point MidPoint(Point start, Point end)
    {
        return new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
    }

    private static Point RotateLocal(Point localPoint, Point center, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point(center.X + localPoint.X * cos - localPoint.Y * sin, center.Y + localPoint.X * sin + localPoint.Y * cos);
    }

    private IEnumerable<VisionOverlayItem> EnumerateItems()
    {
        return ItemsSource?.OfType<VisionOverlayItem>() ?? Enumerable.Empty<VisionOverlayItem>();
    }

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (ResultOverlayCanvas)dependencyObject;
        control.DetachItems(e.OldValue as IEnumerable);
        control.AttachItems(e.NewValue as IEnumerable);
        control.InvalidateVisual();
    }

    private void AttachItems(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged += OnCollectionChanged;
        }

        foreach (var item in items?.OfType<INotifyPropertyChanged>() ?? Enumerable.Empty<INotifyPropertyChanged>())
        {
            item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void DetachItems(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnCollectionChanged;
        }

        foreach (var item in items?.OfType<INotifyPropertyChanged>() ?? Enumerable.Empty<INotifyPropertyChanged>())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }
}
