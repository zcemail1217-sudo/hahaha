using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VisionStation.Vision.UI.Models;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Controls;

public sealed class RoiEditorCanvas : FrameworkElement
{
    public static readonly DependencyProperty ImageFrameProperty = DependencyProperty.Register(
        nameof(ImageFrame),
        typeof(ImageFrame),
        typeof(RoiEditorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(RoiEditorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedRoiProperty = DependencyProperty.Register(
        nameof(SelectedRoi),
        typeof(RoiEditorItem),
        typeof(RoiEditorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlacementCommandProperty = DependencyProperty.Register(
        nameof(PlacementCommand),
        typeof(ICommand),
        typeof(RoiEditorCanvas),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsPlacementArmedProperty = DependencyProperty.Register(
        nameof(IsPlacementArmed),
        typeof(bool),
        typeof(RoiEditorCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsPlacementArmedChanged));

    private readonly Pen _normalPen = new(new SolidColorBrush(Color.FromRgb(35, 211, 245)), 0.65);
    private readonly Pen _selectedPen = new(new SolidColorBrush(Color.FromRgb(66, 229, 142)), 0.8);
    private readonly Pen _polygonPen = new(new SolidColorBrush(Color.FromRgb(255, 200, 87)), 0.7);
    private readonly Pen _rectangle2Pen = new(new SolidColorBrush(Color.FromRgb(255, 176, 0)), 0.7);
    private readonly Pen _templateMaskPen = new(new SolidColorBrush(Color.FromRgb(255, 92, 122)), 0.8);
    private readonly Pen _directionPen = new(new SolidColorBrush(Color.FromRgb(0, 230, 70)), 0.65);
    private readonly Pen _handleLinePen = new(new SolidColorBrush(Color.FromRgb(66, 229, 142)), 0.65);
    private readonly Brush _handleBrush = new SolidColorBrush(Color.FromRgb(66, 229, 142));
    private readonly Brush _redHandleBrush = new SolidColorBrush(Color.FromRgb(255, 0, 0));
    private readonly Brush _labelBrush = new SolidColorBrush(Color.FromArgb(220, 5, 11, 19));
    private readonly Brush _gridBrush = new SolidColorBrush(Color.FromArgb(42, 35, 211, 245));

    private Rect _imageViewport;
    private double _scale = 1;
    private Point _dragStartImagePoint;
    private IReadOnlyList<Point2D> _originalPoints = Array.Empty<Point2D>();
    private double _originalX;
    private double _originalY;
    private double _originalWidth;
    private double _originalHeight;
    private double _originalAngle;
    private double _originalRadius;
    private double _originalCaliperSearchWidth;
    private double _activeRectangleResizeHorizontal;
    private double _activeRectangleResizeVertical;
    private EditMode _editMode = EditMode.None;

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

    public RoiEditorItem? SelectedRoi
    {
        get => (RoiEditorItem?)GetValue(SelectedRoiProperty);
        set => SetValue(SelectedRoiProperty, value);
    }

    public ICommand? PlacementCommand
    {
        get => (ICommand?)GetValue(PlacementCommandProperty);
        set => SetValue(PlacementCommandProperty, value);
    }

    public bool IsPlacementArmed
    {
        get => (bool)GetValue(IsPlacementArmedProperty);
        set => SetValue(IsPlacementArmedProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        UpdateViewport();
        DrawGrid(drawingContext);

        foreach (var roi in EnumerateItems())
        {
            DrawRoi(drawingContext, roi, ReferenceEquals(roi, SelectedRoi));
        }
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        return bounds.Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null!;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        UpdateViewport();
        var screenPoint = e.GetPosition(this);
        if (!_imageViewport.Contains(screenPoint))
        {
            return;
        }

        var imagePoint = ClampToImage(ToImagePoint(screenPoint));
        if (IsPlacementArmed && e.ChangedButton == MouseButton.Left)
        {
            var placementPoint = new Point2D(imagePoint.X, imagePoint.Y);
            if (PlacementCommand?.CanExecute(placementPoint) == true)
            {
                PlacementCommand.Execute(placementPoint);
            }

            if (TryBeginPolygonDrawing(imagePoint, allowFallbackFromItems: true))
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            return;
        }

        if (TryBeginPolygonDrawing(imagePoint))
        {
            e.Handled = true;
            return;
        }

        var hit = HitTestRoi(imagePoint);
        SelectedRoi = hit.Roi;
        if (hit.Roi is null || hit.Mode == EditMode.None)
        {
            ReleaseMouseCapture();
            _editMode = EditMode.None;
            ResetActiveRectangleHandle();
            InvalidateVisual();
            return;
        }

        CaptureMouse();
        _editMode = hit.Mode;
        _activeRectangleResizeHorizontal = hit.Horizontal;
        _activeRectangleResizeVertical = hit.Vertical;
        _dragStartImagePoint = imagePoint;
        CaptureOriginalState(hit.Roi);

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || SelectedRoi is null || _editMode == EditMode.None)
        {
            return;
        }

        var imagePoint = ClampToImage(ToImagePoint(e.GetPosition(this)));
        var deltaX = imagePoint.X - _dragStartImagePoint.X;
        var deltaY = imagePoint.Y - _dragStartImagePoint.Y;

        switch (_editMode)
        {
            case EditMode.Move:
                MoveSelected(deltaX, deltaY);
                break;
            case EditMode.Resize:
                ResizeSelected(imagePoint);
                break;
            case EditMode.ResizeRotatedRectangle:
                ResizeRotatedRectangle(imagePoint);
                break;
            case EditMode.ResizeCircleInner:
                ResizeCircleCaliper(imagePoint, resizeInner: true);
                break;
            case EditMode.ResizeCircleOuter:
                ResizeCircleCaliper(imagePoint, resizeInner: false);
                break;
            case EditMode.Rotate:
                RotateSelected(imagePoint);
                break;
            case EditMode.DrawPolygon:
                AppendFreehandPoint(imagePoint);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var wasDrawingPolygon = _editMode == EditMode.DrawPolygon;
        ReleaseMouseCapture();
        _editMode = EditMode.None;
        ResetActiveRectangleHandle();
        if (wasDrawingPolygon)
        {
            CompletePolygonDrawing();
        }

        e.Handled = true;
    }

    private bool TryBeginPolygonDrawing(Point imagePoint, bool allowFallbackFromItems = false)
    {
        var selectedPolygon = SelectedRoi is { Shape: RoiShapeKind.Polygon, Points.Count: 0 } polygon
            ? polygon
            : allowFallbackFromItems
                ? EnumerateItems().Reverse().FirstOrDefault(roi => roi.Shape == RoiShapeKind.Polygon && roi.Points.Count == 0)
                : null;

        if (selectedPolygon is null)
        {
            return false;
        }

        SelectedRoi = selectedPolygon;
        CaptureMouse();
        _editMode = EditMode.DrawPolygon;
        _dragStartImagePoint = imagePoint;
        selectedPolygon.Points = [new Point2D(imagePoint.X, imagePoint.Y)];
        InvalidateVisual();
        return true;
    }

    private void CompletePolygonDrawing()
    {
        if (SelectedRoi is { Shape: RoiShapeKind.Polygon, Points.Count: < 3 } selectedPolygon)
        {
            selectedPolygon.Points = Array.Empty<Point2D>();
        }
    }

    private void CaptureOriginalState(RoiEditorItem? roi)
    {
        if (roi is null)
        {
            _originalPoints = Array.Empty<Point2D>();
            return;
        }

        _originalX = roi.X;
        _originalY = roi.Y;
        _originalWidth = roi.Width;
        _originalHeight = roi.Height;
        _originalAngle = roi.Angle;
        _originalRadius = roi.Radius;
        _originalCaliperSearchWidth = roi.CaliperSearchWidth;
        _originalPoints = roi.Points.ToArray();
    }

    private void MoveSelected(double deltaX, double deltaY)
    {
        if (SelectedRoi is null)
        {
            return;
        }

        if (SelectedRoi.Shape == RoiShapeKind.Polygon)
        {
            SelectedRoi.Points = _originalPoints.Select(point => new Point2D(point.X + deltaX, point.Y + deltaY)).ToArray();
            return;
        }

        SelectedRoi.X = _originalX + deltaX;
        SelectedRoi.Y = _originalY + deltaY;
    }

    private void ResizeSelected(Point imagePoint)
    {
        if (SelectedRoi is null)
        {
            return;
        }

        if (SelectedRoi.Shape == RoiShapeKind.Circle)
        {
            SelectedRoi.Radius = Distance(new Point(_originalX, _originalY), imagePoint);
            return;
        }

        if (SelectedRoi.Shape == RoiShapeKind.RotatedRectangle)
        {
            var localPoint = ToLocal(imagePoint, new Point(_originalX, _originalY), _originalAngle);
            SelectedRoi.Width = Math.Max(10, Math.Abs(localPoint.X) * 2);
            SelectedRoi.Height = Math.Max(10, Math.Abs(localPoint.Y) * 2);
            return;
        }

        if (SelectedRoi.Shape == RoiShapeKind.Rectangle)
        {
            SelectedRoi.Width = Math.Max(10, imagePoint.X - _originalX);
            SelectedRoi.Height = Math.Max(10, imagePoint.Y - _originalY);
        }
    }

    private void ResizeRotatedRectangle(Point imagePoint)
    {
        if (SelectedRoi?.Shape != RoiShapeKind.RotatedRectangle)
        {
            return;
        }

        var horizontal = _activeRectangleResizeHorizontal;
        var vertical = _activeRectangleResizeVertical;
        if (Math.Abs(horizontal) < 0.001 && Math.Abs(vertical) < 0.001)
        {
            return;
        }

        var originalCenter = new Point(_originalX, _originalY);
        var localPoint = ToLocal(imagePoint, originalCenter, _originalAngle);
        var originalHalfWidth = Math.Max(5, _originalWidth / 2);
        var originalHalfHeight = Math.Max(5, _originalHeight / 2);

        var newWidth = Math.Max(10, _originalWidth);
        var newHeight = Math.Max(10, _originalHeight);
        var centerLocalX = 0.0;
        var centerLocalY = 0.0;

        if (Math.Abs(horizontal) > 0.001)
        {
            var anchorX = -horizontal * originalHalfWidth;
            var width = Math.Abs(localPoint.X - anchorX);
            if (width >= 10)
            {
                newWidth = width;
                centerLocalX = (localPoint.X + anchorX) / 2.0;
            }
        }

        if (Math.Abs(vertical) > 0.001)
        {
            var anchorY = -vertical * originalHalfHeight;
            var height = Math.Abs(localPoint.Y - anchorY);
            if (height >= 10)
            {
                newHeight = height;
                centerLocalY = (localPoint.Y + anchorY) / 2.0;
            }
        }

        var newCenter = RotateLocal(new Point(centerLocalX, centerLocalY), originalCenter, _originalAngle);
        SelectedRoi.X = newCenter.X;
        SelectedRoi.Y = newCenter.Y;
        SelectedRoi.Width = newWidth;
        SelectedRoi.Height = newHeight;
    }

    private void ResizeCircleCaliper(Point imagePoint, bool resizeInner)
    {
        if (SelectedRoi is null)
        {
            return;
        }

        var distance = Distance(new Point(_originalX, _originalY), imagePoint);
        var originalInner = GetInnerRadius(_originalRadius, _originalCaliperSearchWidth);
        var originalOuter = GetOuterRadius(_originalRadius, _originalCaliperSearchWidth);
        if (resizeInner)
        {
            var inner = Math.Clamp(distance, 1, Math.Max(1, originalOuter - 2));
            SelectedRoi.Radius = (inner + originalOuter) / 2.0;
            SelectedRoi.CaliperSearchWidth = Math.Max(2, originalOuter - inner);
            return;
        }

        var outer = Math.Max(distance, originalInner + 2);
        SelectedRoi.Radius = (originalInner + outer) / 2.0;
        SelectedRoi.CaliperSearchWidth = Math.Max(2, outer - originalInner);
    }

    private void RotateSelected(Point imagePoint)
    {
        if (SelectedRoi?.Shape != RoiShapeKind.RotatedRectangle)
        {
            return;
        }

        var angle = Math.Atan2(imagePoint.Y - _originalY, imagePoint.X - _originalX) * 180 / Math.PI;
        SelectedRoi.Angle = NormalizeAngle(angle);
    }

    private void AppendFreehandPoint(Point imagePoint)
    {
        if (SelectedRoi?.Shape != RoiShapeKind.Polygon)
        {
            return;
        }

        var points = SelectedRoi.Points.ToList();
        if (points.Count == 0 || Distance(new Point(points[^1].X, points[^1].Y), imagePoint) >= 4)
        {
            points.Add(new Point2D(imagePoint.X, imagePoint.Y));
            SelectedRoi.Points = points;
        }
    }

    private HitResult HitTestRoi(Point imagePoint)
    {
        foreach (var roi in EnumerateItems().Reverse())
        {
            if (IsCircleCaliper(roi))
            {
                var ringHit = HitTestCircleCaliperRing(roi, imagePoint);
                if (ringHit != EditMode.None)
                {
                    return new HitResult(roi, ringHit);
                }

                var innerHandle = GetCircleInnerHandle(roi, forHitTest: true);
                if (!innerHandle.IsEmpty && innerHandle.Contains(imagePoint))
                {
                    return new HitResult(roi, EditMode.ResizeCircleInner);
                }

                var outerHandle = GetCircleOuterHandle(roi, forHitTest: true);
                if (!outerHandle.IsEmpty && outerHandle.Contains(imagePoint))
                {
                    return new HitResult(roi, EditMode.ResizeCircleOuter);
                }
            }

            if (roi.Shape == RoiShapeKind.RotatedRectangle)
            {
                if (GetRectangle2MoveHandle(roi, forHitTest: true).Contains(imagePoint))
                {
                    return new HitResult(roi, EditMode.Move);
                }

                if (GetRectangle2DirectionHandle(roi, forHitTest: true).Contains(imagePoint)
                    || GetRectangle2AxisHandle(roi, forHitTest: true).Contains(imagePoint))
                {
                    return new HitResult(roi, EditMode.Rotate);
                }

                var rectangleHandle = HitTestRectangle2ResizeHandle(roi, imagePoint);
                if (rectangleHandle.Mode != EditMode.None)
                {
                    return rectangleHandle;
                }
            }

            var resizeHandle = GetResizeHandle(roi);
            if (!resizeHandle.IsEmpty && resizeHandle.Contains(imagePoint))
            {
                return new HitResult(roi, EditMode.Resize);
            }

            if (Contains(roi, imagePoint))
            {
                return new HitResult(roi, EditMode.Move);
            }
        }

        return new HitResult(null, EditMode.None);
    }

    private HitResult HitTestRectangle2ResizeHandle(RoiEditorItem roi, Point imagePoint)
    {
        foreach (var handle in GetRectangle2ResizeHandles(roi, forHitTest: true))
        {
            if (handle.Bounds.Contains(imagePoint))
            {
                return new HitResult(roi, EditMode.ResizeRotatedRectangle, handle.Horizontal, handle.Vertical);
            }
        }

        return new HitResult(null, EditMode.None);
    }

    private EditMode HitTestCircleCaliperRing(RoiEditorItem roi, Point imagePoint)
    {
        var distance = Distance(new Point(roi.X, roi.Y), imagePoint);
        var tolerance = GetScreenSizeInImage(14);
        if (Math.Abs(distance - GetInnerRadius(roi)) <= tolerance)
        {
            return EditMode.ResizeCircleInner;
        }

        if (Math.Abs(distance - GetOuterRadius(roi)) <= tolerance)
        {
            return EditMode.ResizeCircleOuter;
        }

        return EditMode.None;
    }

    private bool Contains(RoiEditorItem roi, Point imagePoint)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Circle => Distance(new Point(roi.X, roi.Y), imagePoint) <= Math.Max(IsCircleCaliper(roi) ? GetOuterRadius(roi) : roi.Radius, 8),
            RoiShapeKind.RotatedRectangle => ContainsRotatedRectangle(roi, imagePoint),
            RoiShapeKind.Polygon => ContainsPolygon(roi.Points, imagePoint),
            _ => GetBounds(roi).Contains(imagePoint)
        };
    }

    private bool ContainsRotatedRectangle(RoiEditorItem roi, Point imagePoint)
    {
        var local = ToLocal(imagePoint, new Point(roi.X, roi.Y), roi.Angle);
        return Math.Abs(local.X) <= roi.Width / 2 && Math.Abs(local.Y) <= roi.Height / 2;
    }

    private static bool ContainsPolygon(IReadOnlyList<Point2D> points, Point imagePoint)
    {
        if (points.Count < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var pi = points[i];
            var pj = points[j];
            if ((pi.Y > imagePoint.Y) != (pj.Y > imagePoint.Y) &&
                imagePoint.X < (pj.X - pi.X) * (imagePoint.Y - pi.Y) / (pj.Y - pi.Y) + pi.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private Rect GetBounds(RoiEditorItem roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Circle => CreateCircleBounds(roi),
            RoiShapeKind.RotatedRectangle => GetRotatedRectangleBounds(roi),
            RoiShapeKind.Polygon when roi.Points.Count > 0 => new Rect(
                new Point(roi.Points.Min(point => point.X), roi.Points.Min(point => point.Y)),
                new Point(roi.Points.Max(point => point.X), roi.Points.Max(point => point.Y))),
            RoiShapeKind.Polygon => Rect.Empty,
            _ => new Rect(roi.X, roi.Y, Math.Max(roi.Width, 10), Math.Max(roi.Height, 10))
        };
    }

    private Rect GetRotatedRectangleBounds(RoiEditorItem roi)
    {
        var corners = GetRotatedRectangleCorners(roi);
        return new Rect(
            new Point(corners.Min(point => point.X), corners.Min(point => point.Y)),
            new Point(corners.Max(point => point.X), corners.Max(point => point.Y)));
    }

    private static Rect CreateCircleBounds(RoiEditorItem roi)
    {
        var radius = IsCircleCaliper(roi) ? GetOuterRadius(roi) : roi.Radius;
        return new Rect(roi.X - radius, roi.Y - radius, radius * 2, radius * 2);
    }

    private Rect GetResizeHandle(RoiEditorItem roi)
    {
        var handleSize = GetHandleSizeInImage();
        if (roi.Shape == RoiShapeKind.Circle)
        {
            if (IsCircleCaliper(roi))
            {
                return Rect.Empty;
            }

            return CenteredRect(new Point(roi.X + roi.Radius, roi.Y), handleSize);
        }

        if (roi.Shape == RoiShapeKind.RotatedRectangle)
        {
            return CenteredRect(RotateLocal(new Point(roi.Width / 2, roi.Height / 2), new Point(roi.X, roi.Y), roi.Angle), handleSize);
        }

        if (roi.Shape == RoiShapeKind.Rectangle)
        {
            var bounds = GetBounds(roi);
            return CenteredRect(new Point(bounds.Right, bounds.Bottom), handleSize);
        }

        return Rect.Empty;
    }

    private Rect GetCircleInnerHandle(RoiEditorItem roi, bool forHitTest = false)
    {
        if (!IsCircleCaliper(roi))
        {
            return Rect.Empty;
        }

        var handleSize = forHitTest ? GetScreenSizeInImage(18) : GetCircleCaliperHandleSizeInImage();
        return CenteredRect(new Point(roi.X + GetInnerRadius(roi), roi.Y), handleSize);
    }

    private Rect GetCircleOuterHandle(RoiEditorItem roi, bool forHitTest = false)
    {
        if (!IsCircleCaliper(roi))
        {
            return Rect.Empty;
        }

        var handleSize = forHitTest ? GetScreenSizeInImage(18) : GetCircleCaliperHandleSizeInImage();
        return CenteredRect(new Point(roi.X + GetOuterRadius(roi), roi.Y), handleSize);
    }

    private IEnumerable<Rectangle2ResizeHandle> GetRectangle2ResizeHandles(RoiEditorItem roi, bool forHitTest = false)
    {
        var halfWidth = roi.Width / 2;
        var halfHeight = roi.Height / 2;
        var center = new Point(roi.X, roi.Y);
        var handleSize = forHitTest ? GetRectangle2HitSizeInImage() : GetHandleSizeInImage();
        var handles = new[]
        {
            new Rectangle2ResizeHandle(-1, -1),
            new Rectangle2ResizeHandle(0, -1),
            new Rectangle2ResizeHandle(1, -1),
            new Rectangle2ResizeHandle(-1, 0),
            new Rectangle2ResizeHandle(1, 0),
            new Rectangle2ResizeHandle(-1, 1),
            new Rectangle2ResizeHandle(0, 1),
            new Rectangle2ResizeHandle(1, 1)
        };

        foreach (var handle in handles)
        {
            var localPoint = new Point(handle.Horizontal * halfWidth, handle.Vertical * halfHeight);
            var imagePoint = RotateLocal(localPoint, center, roi.Angle);
            yield return handle with { Bounds = CenteredRect(imagePoint, handleSize) };
        }
    }

    private Rect GetRectangle2MoveHandle(RoiEditorItem roi, bool forHitTest = false)
    {
        var handleSize = forHitTest ? GetRectangle2HitSizeInImage() : GetHandleSizeInImage();
        return CenteredRect(new Point(roi.X, roi.Y), handleSize);
    }

    private Rect GetRectangle2AxisHandle(RoiEditorItem roi, bool forHitTest = false)
    {
        var handleSize = forHitTest ? GetRectangle2HitSizeInImage() : GetHandleSizeInImage();
        var handlePoint = RotateLocal(new Point(roi.Width / 4, 0), new Point(roi.X, roi.Y), roi.Angle);
        return CenteredRect(handlePoint, handleSize);
    }

    private Rect GetRectangle2DirectionHandle(RoiEditorItem roi, bool forHitTest = false)
    {
        var handleSize = forHitTest ? GetRectangle2HitSizeInImage() : GetHandleSizeInImage() + 4;
        var handlePoint = RotateLocal(new Point(roi.Width / 2, 0), new Point(roi.X, roi.Y), roi.Angle);
        return CenteredRect(handlePoint, handleSize);
    }

    private Rect GetDirectionHandle(RoiEditorItem roi)
    {
        return GetRectangle2DirectionHandle(roi);
    }

    private double GetHandleSizeInImage()
    {
        return GetScreenSizeInImage(5);
    }

    private double GetCircleCaliperHandleSizeInImage()
    {
        return GetScreenSizeInImage(3);
    }

    private double GetRectangle2HitSizeInImage()
    {
        return GetScreenSizeInImage(16);
    }

    private double GetScreenSizeInImage(double screenPixels)
    {
        return screenPixels / Math.Max(GetEffectiveImageScale(), 0.001);
    }

    private double GetEffectiveImageScale()
    {
        return _scale * GetAncestorZoomScale();
    }

    private double GetAncestorZoomScale()
    {
        var scale = 1.0;
        DependencyObject? current = this;
        while ((current = VisualTreeHelper.GetParent(current)) is not null)
        {
            if (current is UIElement visual)
            {
                var matrix = visual.RenderTransform.Value;
                if (!matrix.IsIdentity)
                {
                    var xScale = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
                    var yScale = Math.Sqrt(matrix.M21 * matrix.M21 + matrix.M22 * matrix.M22);
                    scale *= Math.Max(0.001, (xScale + yScale) / 2.0);
                }
            }

            if (current is ZoomableImageSurface)
            {
                break;
            }
        }

        return Math.Max(0.001, scale);
    }

    private static Rect CenteredRect(Point center, double size)
    {
        return new Rect(center.X - size / 2, center.Y - size / 2, size, size);
    }

    private void DrawGrid(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(null, new Pen(_gridBrush, 1), _imageViewport);

        if (_imageViewport.Width <= 0 || _imageViewport.Height <= 0)
        {
            return;
        }

        var pen = new Pen(_gridBrush, 1);
        for (var i = 1; i < 4; i++)
        {
            var x = _imageViewport.Left + _imageViewport.Width * i / 4;
            drawingContext.DrawLine(pen, new Point(x, _imageViewport.Top), new Point(x, _imageViewport.Bottom));
        }

        for (var i = 1; i < 4; i++)
        {
            var y = _imageViewport.Top + _imageViewport.Height * i / 4;
            drawingContext.DrawLine(pen, new Point(_imageViewport.Left, y), new Point(_imageViewport.Right, y));
        }
    }

    private void DrawRoi(DrawingContext drawingContext, RoiEditorItem roi, bool selected)
    {
        var isTemplateMask = roi.Name.StartsWith("模板掩膜", StringComparison.Ordinal);
        var pen = roi.Shape switch
        {
            _ when isTemplateMask => _templateMaskPen,
            RoiShapeKind.Polygon => _polygonPen,
            RoiShapeKind.RotatedRectangle => _rectangle2Pen,
            _ => selected ? _selectedPen : _normalPen
        };
        var fill = isTemplateMask
            ? new SolidColorBrush(Color.FromArgb((byte)(selected ? 34 : 22), 255, 92, 122))
            : selected
                ? new SolidColorBrush(Color.FromArgb(20, 66, 229, 142))
                : new SolidColorBrush(Color.FromArgb(12, 35, 211, 245));

        switch (roi.Shape)
        {
            case RoiShapeKind.Circle:
                if (IsCircleCaliper(roi))
                {
                    DrawCircleCaliperEditor(drawingContext, roi, pen);
                }
                else
                {
                    drawingContext.DrawEllipse(fill, pen, ToScreenPoint(new Point(roi.X, roi.Y)), roi.Radius * _scale, roi.Radius * _scale);
                }
                break;
            case RoiShapeKind.Polygon:
                DrawPolygon(drawingContext, roi, pen, fill);
                break;
            case RoiShapeKind.RotatedRectangle:
                DrawRotatedRectangle(drawingContext, roi, pen, fill);
                break;
            default:
                DrawRectangle(drawingContext, roi, pen, fill);
                break;
        }

        DrawLabel(drawingContext, roi);

        if (selected)
        {
            if (roi.Shape == RoiShapeKind.RotatedRectangle)
            {
                DrawRectangle2Direction(drawingContext, roi);
                DrawRectangle2Handles(drawingContext, roi);
            }
            else if (IsCircleCaliper(roi))
            {
                DrawHandle(drawingContext, GetCircleInnerHandle(roi), _handleBrush);
                DrawHandle(drawingContext, GetCircleOuterHandle(roi), _handleBrush);
            }
            else
            {
                DrawHandle(drawingContext, GetResizeHandle(roi), _handleBrush);
            }
        }
    }

    private void DrawCircleCaliperEditor(DrawingContext drawingContext, RoiEditorItem roi, Pen pen)
    {
        var center = ToScreenPoint(new Point(roi.X, roi.Y));
        var innerRadius = GetInnerRadius(roi);
        var outerRadius = GetOuterRadius(roi);
        drawingContext.DrawEllipse(null, pen, center, innerRadius * _scale, innerRadius * _scale);
        drawingContext.DrawEllipse(null, pen, center, outerRadius * _scale, outerRadius * _scale);
        DrawCircleCenterMarker(drawingContext, roi);
    }

    private void DrawCircleCenterMarker(DrawingContext drawingContext, RoiEditorItem roi)
    {
        var center = new Point(roi.X, roi.Y);
        var size = GetScreenSizeInImage(4);
        var radius = GetScreenSizeInImage(1.2);
        var screenCenter = ToScreenPoint(center);
        drawingContext.DrawLine(_handleLinePen, ToScreenPoint(new Point(center.X - size, center.Y)), ToScreenPoint(new Point(center.X + size, center.Y)));
        drawingContext.DrawLine(_handleLinePen, ToScreenPoint(new Point(center.X, center.Y - size)), ToScreenPoint(new Point(center.X, center.Y + size)));
        drawingContext.DrawEllipse(_handleBrush, null, screenCenter, radius * _scale, radius * _scale);
    }

    private void DrawRectangle(DrawingContext drawingContext, RoiEditorItem roi, Pen pen, Brush fill)
    {
        var topLeft = ToScreenPoint(new Point(roi.X, roi.Y));
        drawingContext.DrawRectangle(fill, pen, new Rect(topLeft.X, topLeft.Y, roi.Width * _scale, roi.Height * _scale));
    }

    private void DrawRotatedRectangle(DrawingContext drawingContext, RoiEditorItem roi, Pen pen, Brush fill)
    {
        var center = ToScreenPoint(new Point(roi.X, roi.Y));
        var rect = new Rect(center.X - roi.Width * _scale / 2, center.Y - roi.Height * _scale / 2, roi.Width * _scale, roi.Height * _scale);
        drawingContext.PushTransform(new RotateTransform(roi.Angle, center.X, center.Y));
        drawingContext.DrawRectangle(fill, pen, rect);
        drawingContext.Pop();
    }

    private void DrawRectangle2Handles(DrawingContext drawingContext, RoiEditorItem roi)
    {
        var halfWidth = roi.Width / 2;
        var halfHeight = roi.Height / 2;
        var center = new Point(roi.X, roi.Y);
        var localHandles = new[]
        {
            new Point(-halfWidth, -halfHeight),
            new Point(0, -halfHeight),
            new Point(halfWidth, -halfHeight),
            new Point(halfWidth, 0),
            new Point(halfWidth, halfHeight),
            new Point(0, halfHeight),
            new Point(-halfWidth, halfHeight),
            new Point(-halfWidth, 0)
        };

        var handleSize = GetHandleSizeInImage();
        foreach (var localHandle in localHandles)
        {
            DrawHandle(drawingContext, CenteredRect(RotateLocal(localHandle, center, roi.Angle), handleSize), _redHandleBrush);
        }
    }

    private void DrawRectangle2Direction(DrawingContext drawingContext, RoiEditorItem roi)
    {
        var center = new Point(roi.X, roi.Y);
        var axisEnd = RotateLocal(new Point(roi.Width / 2, 0), center, roi.Angle);
        var axisMid = RotateLocal(new Point(roi.Width / 4, 0), center, roi.Angle);

        drawingContext.DrawLine(_directionPen, ToScreenPoint(center), ToScreenPoint(axisEnd));
        DrawArrowHead(drawingContext, center, axisEnd);

        var handleSize = GetHandleSizeInImage();
        DrawHandle(drawingContext, CenteredRect(center, handleSize), _redHandleBrush);
        DrawHandle(drawingContext, CenteredRect(axisMid, handleSize), _redHandleBrush);
        DrawHandle(drawingContext, GetDirectionHandle(roi), _redHandleBrush);
    }

    private void DrawArrowHead(DrawingContext drawingContext, Point start, Point end)
    {
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var length = 16 / Math.Max(_scale, 0.001);
        var spread = Math.PI / 7;
        var p1 = new Point(end.X - length * Math.Cos(angle - spread), end.Y - length * Math.Sin(angle - spread));
        var p2 = new Point(end.X - length * Math.Cos(angle + spread), end.Y - length * Math.Sin(angle + spread));

        drawingContext.DrawLine(_directionPen, ToScreenPoint(end), ToScreenPoint(p1));
        drawingContext.DrawLine(_directionPen, ToScreenPoint(end), ToScreenPoint(p2));
    }

    private void DrawPolygon(DrawingContext drawingContext, RoiEditorItem roi, Pen pen, Brush fill)
    {
        if (roi.Points.Count == 0)
        {
            return;
        }

        if (roi.Points.Count == 1)
        {
            drawingContext.DrawEllipse(fill, pen, ToScreenPoint(new Point(roi.Points[0].X, roi.Points[0].Y)), 3, 3);
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var first = ToScreenPoint(new Point(roi.Points[0].X, roi.Points[0].Y));
            var isClosed = roi.Points.Count > 2;
            context.BeginFigure(first, isClosed, isClosed);
            context.PolyLineTo(roi.Points.Skip(1).Select(point => ToScreenPoint(new Point(point.X, point.Y))).ToArray(), true, true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(roi.Points.Count > 2 ? fill : null, pen, geometry);
    }

    private void DrawLabel(DrawingContext drawingContext, RoiEditorItem roi)
    {
        var bounds = GetBounds(roi);
        if (bounds.IsEmpty)
        {
            return;
        }

        var labelPoint = ToScreenPoint(new Point(bounds.X, bounds.Y));
        var text = new FormattedText(
            roi.Name,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            12,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var labelRect = new Rect(labelPoint.X, Math.Max(0, labelPoint.Y - 22), text.Width + 14, 20);
        drawingContext.DrawRoundedRectangle(_labelBrush, null, labelRect, 4, 4);
        drawingContext.DrawText(text, new Point(labelRect.Left + 7, labelRect.Top + 2));
    }

    private void DrawHandle(DrawingContext drawingContext, Rect imageHandle, Brush brush)
    {
        if (imageHandle.IsEmpty)
        {
            return;
        }

        var topLeft = ToScreenPoint(new Point(imageHandle.X, imageHandle.Y));
        var rect = new Rect(topLeft.X, topLeft.Y, imageHandle.Width * _scale, imageHandle.Height * _scale);
        drawingContext.DrawRectangle(brush, null, rect);
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

    private Point ToImagePoint(Point screenPoint)
    {
        if (_scale <= 0)
        {
            return new Point();
        }

        return new Point((screenPoint.X - _imageViewport.Left) / _scale, (screenPoint.Y - _imageViewport.Top) / _scale);
    }

    private Point ClampToImage(Point imagePoint)
    {
        var frame = ImageFrame;
        if (frame is null)
        {
            return imagePoint;
        }

        return new Point(Math.Clamp(imagePoint.X, 0, frame.Width), Math.Clamp(imagePoint.Y, 0, frame.Height));
    }

    private static Point RotateLocal(Point localPoint, Point center, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Point(center.X + localPoint.X * cos - localPoint.Y * sin, center.Y + localPoint.X * sin + localPoint.Y * cos);
    }

    private static Point ToLocal(Point imagePoint, Point center, double angleDegrees)
    {
        var radians = -angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var dx = imagePoint.X - center.X;
        var dy = imagePoint.Y - center.Y;
        return new Point(dx * cos - dy * sin, dx * sin + dy * cos);
    }

    private static Point[] GetRotatedRectangleCorners(RoiEditorItem roi)
    {
        var center = new Point(roi.X, roi.Y);
        var halfWidth = roi.Width / 2;
        var halfHeight = roi.Height / 2;
        return
        [
            RotateLocal(new Point(-halfWidth, -halfHeight), center, roi.Angle),
            RotateLocal(new Point(halfWidth, -halfHeight), center, roi.Angle),
            RotateLocal(new Point(halfWidth, halfHeight), center, roi.Angle),
            RotateLocal(new Point(-halfWidth, halfHeight), center, roi.Angle)
        ];
    }

    private static bool IsCircleCaliper(RoiEditorItem roi)
    {
        return roi.Shape == RoiShapeKind.Circle && roi.CaliperSearchWidth > 0;
    }

    private static double GetInnerRadius(RoiEditorItem roi)
    {
        return GetInnerRadius(roi.Radius, roi.CaliperSearchWidth);
    }

    private static double GetOuterRadius(RoiEditorItem roi)
    {
        return GetOuterRadius(roi.Radius, roi.CaliperSearchWidth);
    }

    private static double GetInnerRadius(double radius, double searchWidth)
    {
        return Math.Max(1, radius - searchWidth / 2.0);
    }

    private static double GetOuterRadius(double radius, double searchWidth)
    {
        return Math.Max(GetInnerRadius(radius, searchWidth) + 1, radius + searchWidth / 2.0);
    }

    private static double Distance(Point a, Point b)
    {
        return Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }

        while (angle <= -180)
        {
            angle += 360;
        }

        return angle;
    }

    private void ResetActiveRectangleHandle()
    {
        _activeRectangleResizeHorizontal = 0;
        _activeRectangleResizeVertical = 0;
    }

    private IEnumerable<RoiEditorItem> EnumerateItems()
    {
        return ItemsSource?.OfType<RoiEditorItem>() ?? Enumerable.Empty<RoiEditorItem>();
    }

    private static void OnItemsSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (RoiEditorCanvas)dependencyObject;
        control.DetachItems(e.OldValue as IEnumerable);
        control.AttachItems(e.NewValue as IEnumerable);
        control.InvalidateVisual();
    }

    private static void OnIsPlacementArmedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (RoiEditorCanvas)dependencyObject;
        control.Cursor = (bool)e.NewValue ? Cursors.Cross : null;
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
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>())
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        InvalidateVisual();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private sealed record HitResult(RoiEditorItem? Roi, EditMode Mode, double Horizontal = 0, double Vertical = 0);

    private readonly record struct Rectangle2ResizeHandle(double Horizontal, double Vertical)
    {
        public Rect Bounds { get; init; }
    }

    private enum EditMode
    {
        None,
        Move,
        Resize,
        ResizeRotatedRectangle,
        ResizeCircleInner,
        ResizeCircleOuter,
        Rotate,
        DrawPolygon
    }
}
