using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DarkSign.Launcher;

/// <summary>
/// The build progress Darksign: the ring of fire (Assets/darksign.png) starts as faint embers and ignites clockwise
/// from the top as the build goes, a full burning ring at 100%. The percentage sits inside the ring in Cinzel.
/// </summary>
public sealed class DarksignProgress : Viewbox
{
    // Coordinates are the image's own pixels (512 x 512); the ring's centre in the picture is about (257, 275).
    private const double ImageSize = 512, CenterX = 257, CenterY = 275, ClipRadius = 400;

    /// <summary>Cinzel (SIL OFL 1.1), a free classical-capitals font close to Elden Ring's titles.</summary>
    public static readonly FontFamily Cinzel = new(new Uri("pack://application:,,,/"), "./Assets/#Cinzel SemiBold, ./Assets/#Cinzel, Georgia");

    private readonly Image _lit;
    private readonly TextBlock _percent;
    private double _fraction = -1;

    public DarksignProgress()
    {
        var source = new BitmapImage(new Uri("pack://application:,,,/Assets/darksign.png"));
        var grid = new Grid { Width = ImageSize, Height = ImageSize, Background = Brushes.Transparent };

        // Unlit: the same ring, dim and cold, so the shape is there before it burns.
        grid.Children.Add(new Image { Source = source, Opacity = 0.16, Width = ImageSize, Height = ImageSize });

        _lit = new Image
        {
            Source = source,
            Width = ImageSize,
            Height = ImageSize,
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x7A, 0x1A), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.75 },
        };
        grid.Children.Add(_lit);

        _percent = new TextBlock
        {
            FontFamily = Cinzel,
            FontWeight = FontWeights.SemiBold,
            FontSize = 92,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x7A)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Centred on the ring, not on the picture.
            Margin = new Thickness((CenterX - ImageSize / 2) * 2, (CenterY - ImageSize / 2) * 2, 0, 0),
            Effect = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x6A, 0x00), BlurRadius = 24, ShadowDepth = 0, Opacity = 0.8 },
            Text = "0%",
        };
        grid.Children.Add(_percent);
        Child = grid;
        Stretch = Stretch.Uniform;

        // The lit part flickers like a fire.
        var flicker = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1.6) };
        foreach (var (t, v) in new[] { (0.0, 1.0), (0.25, 0.86), (0.5, 0.97), (0.8, 0.82), (1.1, 1.0), (1.35, 0.9), (1.6, 1.0) })
            flicker.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        _lit.BeginAnimation(OpacityProperty, flicker);

        Fraction = 0;
    }

    /// <summary>0..1: how much of the ring is burning.</summary>
    public double Fraction
    {
        get => _fraction;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _fraction) < 0.0005)
                return;
            _fraction = value;
            _percent.Text = $"{Math.Floor(value * 100):0}%";
            _lit.Clip = value >= 0.999 ? null : Pie(value);
        }
    }

    /// <summary>A slice from 12 o'clock, clockwise, covering <paramref name="fraction"/> of the turn.</summary>
    private static Geometry Pie(double fraction)
    {
        if (fraction <= 0)
            return Geometry.Empty;
        var angle = fraction * 2 * Math.PI;
        var start = new Point(CenterX, CenterY - ClipRadius);
        var end = new Point(CenterX + ClipRadius * Math.Sin(angle), CenterY - ClipRadius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(CenterX, CenterY), isFilled: true, isClosed: true);
            ctx.LineTo(start, false, false);
            ctx.ArcTo(end, new Size(ClipRadius, ClipRadius), 0, isLargeArc: fraction > 0.5, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
