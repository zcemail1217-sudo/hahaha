using Prism.Mvvm;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.Models;

public sealed class RoiEditorItem : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "ROI";
    private RoiShapeKind _shape = RoiShapeKind.Rectangle;
    private double _x;
    private double _y;
    private double _width = 120;
    private double _height = 80;
    private double _angle;
    private double _radius = 60;
    private double _caliperSearchWidth;
    private IReadOnlyList<Point2D> _points = Array.Empty<Point2D>();

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public RoiShapeKind Shape
    {
        get => _shape;
        set
        {
            if (SetProperty(ref _shape, value))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double X
    {
        get => _x;
        set
        {
            if (SetProperty(ref _x, Math.Round(value, 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double Y
    {
        get => _y;
        set
        {
            if (SetProperty(ref _y, Math.Round(value, 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, Math.Round(Math.Max(10, value), 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, Math.Round(Math.Max(10, value), 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double Angle
    {
        get => _angle;
        set
        {
            if (SetProperty(ref _angle, Math.Round(value, 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double Radius
    {
        get => _radius;
        set
        {
            if (SetProperty(ref _radius, Math.Round(Math.Max(5, value), 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public double CaliperSearchWidth
    {
        get => _caliperSearchWidth;
        set
        {
            if (SetProperty(ref _caliperSearchWidth, Math.Round(Math.Max(0, value), 2)))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public IReadOnlyList<Point2D> Points
    {
        get => _points;
        set
        {
            if (SetProperty(ref _points, value.ToArray()))
            {
                RaiseGeometryChanged();
            }
        }
    }

    public string Geometry => Shape switch
    {
        RoiShapeKind.Polygon => $"{Points.Count} points",
        RoiShapeKind.Circle => $"CX={X:0.#}, CY={Y:0.#}, R={Radius:0.#}",
        RoiShapeKind.RotatedRectangle => $"CX={X:0.#}, CY={Y:0.#}, W={Width:0.#}, H={Height:0.#}, A={Angle:0.#}",
        _ => $"X={X:0.#}, Y={Y:0.#}, W={Width:0.#}, H={Height:0.#}"
    };

    public static RoiEditorItem FromDefinition(RoiDefinition definition)
    {
        return new RoiEditorItem
        {
            Id = definition.Id,
            Name = definition.Name,
            Shape = definition.Shape,
            X = definition.X,
            Y = definition.Y,
            Width = definition.Width,
            Height = definition.Height,
            Angle = definition.Angle,
            Radius = definition.Radius,
            Points = definition.Points
        };
    }

    public RoiDefinition ToDefinition()
    {
        return new RoiDefinition
        {
            Id = Id,
            Name = Name,
            Shape = Shape,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            Angle = Angle,
            Radius = Radius,
            Points = Points.ToArray()
        };
    }

    public void MoveBy(double deltaX, double deltaY)
    {
        if (Shape == RoiShapeKind.Polygon)
        {
            Points = Points.Select(point => new Point2D(point.X + deltaX, point.Y + deltaY)).ToArray();
            return;
        }

        X += deltaX;
        Y += deltaY;
    }

    private void RaiseGeometryChanged()
    {
        RaisePropertyChanged(nameof(Geometry));
    }
}
