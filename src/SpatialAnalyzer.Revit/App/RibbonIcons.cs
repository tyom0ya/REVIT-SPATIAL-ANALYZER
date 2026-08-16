using System.Windows;
using System.Windows.Media;

namespace SpatialAnalyzer.Revit.App;

/// <summary>
/// The ribbon's icons, drawn as geometry rather than shipped as pictures.
///
/// Revit asks for an ImageSource, which is a WPF type. The contract rules out
/// WPF for the first version, and that was written to mean windows and dialogs:
/// this project's user interface is Revit's own task dialogs and nothing else,
/// and no window here is ours. An icon is not an interface.
///
/// Drawing them rather than embedding files keeps the repository free of binary
/// assets that cannot be read in a diff, and a vector drawing stays sharp at
/// whatever size Revit asks for - the same object serves the small and large
/// button. Each icon below is a few shapes, and what it is meant to depict is
/// stated where a picture could not say so.
///
/// The palette is the analysis's own: red for what encloses a space, yellow for
/// what is inside it, green for a way through. An icon that used different
/// colours from the drawing would have to be learned separately.
/// </summary>
internal static class RibbonIcons
{
    private static readonly Brush Encloses = Frozen(new SolidColorBrush(Color.FromRgb(220, 30, 30)));
    private static readonly Brush Inside = Frozen(new SolidColorBrush(Color.FromRgb(230, 200, 0)));
    private static readonly Brush Way = Frozen(new SolidColorBrush(Color.FromRgb(0, 170, 60)));
    private static readonly Brush Neutral = Frozen(new SolidColorBrush(Color.FromRgb(70, 70, 76)));
    private static readonly Brush Accent = Frozen(new SolidColorBrush(Color.FromRgb(30, 110, 200)));
    private static readonly Brush Outline = Frozen(new SolidColorBrush(Color.FromRgb(200, 0, 200)));
    private static readonly Brush Paper = Frozen(new SolidColorBrush(Color.FromRgb(250, 250, 252)));

    /// <summary>Whether a room's boundary can be checked, before analysing it.</summary>
    public static ImageSource Preflight { get; } = Draw(
        Filled(Circle(16, 16, 13), Way),
        Stroked(Polyline(9, 16, 14, 21, 23, 11), Paper, 3.2));

    /// <summary>One element, picked.</summary>
    public static ImageSource InspectElement { get; } = Draw(
        Stroked(Rect(5, 5, 22, 22), Neutral, 2.2),
        Filled(Rect(10, 10, 8, 8), Accent),
        Stroked(Polyline(17, 17, 26, 26), Neutral, 2.6),
        Filled(Polygon(24, 22, 28, 28, 22, 24), Neutral));

    /// <summary>A written record of what is in the model.</summary>
    public static ImageSource AuditModel { get; } = Draw(
        Filled(Rect(7, 4, 18, 24), Paper),
        Stroked(Rect(7, 4, 18, 24), Neutral, 2),
        Stroked(Polyline(11, 11, 21, 11), Neutral, 1.8),
        Stroked(Polyline(11, 16, 21, 16), Neutral, 1.8),
        Stroked(Polyline(11, 21, 17, 21), Neutral, 1.8));

    /// <summary>The plan divided into the regions the walls produce.</summary>
    public static ImageSource ProbeCircuits { get; } = Draw(
        Filled(Rect(4, 4, 11, 11), Accent),
        Filled(Rect(17, 4, 11, 11), Neutral),
        Filled(Rect(4, 17, 11, 11), Neutral),
        Filled(Rect(17, 17, 11, 11), Accent));

    /// <summary>Regions traced on the plan, in the colour they are drawn in.</summary>
    public static ImageSource OutlineRegions { get; } = Draw(
        Stroked(Rect(4, 6, 24, 20), Outline, 2.6),
        Stroked(Polyline(16, 6, 16, 26), Outline, 2.6),
        Filled(Circle(10, 16, 2.4), Outline),
        Filled(Circle(22, 16, 2.4), Outline));

    /// <summary>A region judged to be a room, or not.</summary>
    public static ImageSource QualifyRegions { get; } = Draw(
        Stroked(Rect(4, 5, 20, 22), Neutral, 2.2),
        Filled(Rect(4, 13, 4, 7), Way),
        Stroked(Polyline(16, 18, 20, 23, 28, 10), Way, 3.4));

    /// <summary>An element, and the room it turns out to be in.</summary>
    public static ImageSource AnalyzeSelection { get; } = Draw(
        Stroked(Rect(4, 5, 24, 22), Neutral, 2.2),
        Filled(Rect(4, 12, 3.5, 8), Way),
        Filled(Circle(19, 16, 4.5), Inside),
        Stroked(Circle(19, 16, 4.5), Neutral, 1.6));

    /// <summary>The room coloured on the plan: encloses, inside, way through.</summary>
    public static ImageSource HighlightRoom { get; } = Draw(
        Filled(Rect(5, 6, 22, 20), Inside),
        Stroked(Rect(5, 6, 22, 20), Encloses, 3),
        Filled(Rect(3.5, 13, 4, 7), Way));

    /// <summary>The colouring taken off again.</summary>
    public static ImageSource ClearHighlight { get; } = Draw(
        Stroked(Rect(5, 6, 22, 20), Neutral, 2.2),
        Stroked(Polyline(7, 27, 25, 5), Encloses, 3.2));

    /// <summary>The analysis written out to a file.</summary>
    public static ImageSource ExportAnalysis { get; } = Draw(
        Filled(Rect(5, 3, 17, 20), Paper),
        Stroked(Rect(5, 3, 17, 20), Neutral, 2),
        Stroked(Polyline(9, 9, 18, 9), Neutral, 1.8),
        Stroked(Polyline(9, 14, 18, 14), Neutral, 1.8),
        Stroked(Polyline(24, 16, 24, 26), Accent, 2.6),
        Filled(Polygon(20, 24, 28, 24, 24, 30), Accent));

    /// <summary>
    /// A wall Revit walks past when working out rooms: drawn as a room whose
    /// dividing wall is dashed, so the space reads as one when it is two.
    /// </summary>
    public static ImageSource RoomBounding { get; } = Draw(
        Stroked(Rect(4, 6, 24, 20), Neutral, 2.2),
        Stroked(Polyline(16, 7, 16, 11), Encloses, 2.4),
        Stroked(Polyline(16, 14, 16, 18), Encloses, 2.4),
        Stroked(Polyline(16, 21, 16, 25), Encloses, 2.4));

    /// <summary>Everything built to interrogate the model rather than report on it.</summary>
    public static ImageSource Diagnostics { get; } = Draw(
        Stroked(Circle(14, 14, 9), Neutral, 2.6),
        Stroked(Polyline(20, 20, 28, 28), Neutral, 3),
        Filled(Circle(14, 14, 4.5), Accent));

    private static ImageSource Draw(params Drawing[] parts)
    {
        var group = new DrawingGroup();
        foreach (Drawing part in parts)
        {
            group.Children.Add(part);
        }

        // Frozen so it can be handed to Revit's user interface thread without
        // the risk of it being altered afterwards, and so WPF need not track it
        // for changes it will never receive.
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static Drawing Filled(Geometry geometry, Brush brush) =>
        Frozen(new GeometryDrawing(brush, null, Frozen(geometry)));

    private static Drawing Stroked(Geometry geometry, Brush brush, double thickness) =>
        Frozen(new GeometryDrawing(
            null,
            Frozen(new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }),
            Frozen(geometry)));

    private static Geometry Rect(double x, double y, double width, double height) =>
        new RectangleGeometry(new Rect(x, y, width, height), 1.5, 1.5);

    private static Geometry Circle(double x, double y, double radius) =>
        new EllipseGeometry(new Point(x, y), radius, radius);

    private static Geometry Polyline(params double[] coordinates) => Path(coordinates, closed: false, filled: false);

    private static Geometry Polygon(params double[] coordinates) => Path(coordinates, closed: true, filled: true);

    private static Geometry Path(double[] coordinates, bool closed, bool filled)
    {
        var figure = new PathFigure { StartPoint = new Point(coordinates[0], coordinates[1]), IsClosed = closed, IsFilled = filled };

        for (int i = 2; i < coordinates.Length; i += 2)
        {
            figure.Segments.Add(new LineSegment(new Point(coordinates[i], coordinates[i + 1]), true));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
