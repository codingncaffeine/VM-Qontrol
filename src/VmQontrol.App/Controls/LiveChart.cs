using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace VmQontrol.App.Controls;

/// <summary>
/// A live line chart of the last minute: one or two series, 2 px lines on a recessive grid, newest sample at the right,
/// one y-axis. Hovering shows a crosshair, a dot on each series and their values. Text layouts are cached: they are
/// built when the values or the hovered sample change, never on every frame.
/// </summary>
public sealed class LiveChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> Series1Property = AvaloniaProperty.Register<LiveChart, IReadOnlyList<double>?>(nameof(Series1));
    public static readonly StyledProperty<IReadOnlyList<double>?> Series2Property = AvaloniaProperty.Register<LiveChart, IReadOnlyList<double>?>(nameof(Series2));
    public static readonly StyledProperty<IBrush?> Series1BrushProperty = AvaloniaProperty.Register<LiveChart, IBrush?>(nameof(Series1Brush));
    public static readonly StyledProperty<IBrush?> Series2BrushProperty = AvaloniaProperty.Register<LiveChart, IBrush?>(nameof(Series2Brush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty = AvaloniaProperty.Register<LiveChart, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<LiveChart, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<LiveChart, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<LiveChart, double>(nameof(Maximum));
    public static readonly StyledProperty<string> Series1NameProperty = AvaloniaProperty.Register<LiveChart, string>(nameof(Series1Name), "");
    public static readonly StyledProperty<string> Series2NameProperty = AvaloniaProperty.Register<LiveChart, string>(nameof(Series2Name), "");
    public static readonly StyledProperty<Func<double, string>?> FormatProperty = AvaloniaProperty.Register<LiveChart, Func<double, string>?>(nameof(Format));

    /// <summary>Samples shown across the width (one a second: the last minute).</summary>
    public const int Capacity = 60;

    private int? _hover;
    private TextLayout? _hoverText;
    private TextLayout? _maxText;
    private double _maxTextFor = double.NaN;

    static LiveChart()
    {
        AffectsRender<LiveChart>(Series1Property, Series2Property, Series1BrushProperty, Series2BrushProperty, GridBrushProperty, MaximumProperty);
        Series1Property.Changed.AddClassHandler<LiveChart>((c, _) => c._hoverText = null);
        Series2Property.Changed.AddClassHandler<LiveChart>((c, _) => c._hoverText = null);
    }

    public IReadOnlyList<double>? Series1 { get => GetValue(Series1Property); set => SetValue(Series1Property, value); }

    public IReadOnlyList<double>? Series2 { get => GetValue(Series2Property); set => SetValue(Series2Property, value); }

    public IBrush? Series1Brush { get => GetValue(Series1BrushProperty); set => SetValue(Series1BrushProperty, value); }

    public IBrush? Series2Brush { get => GetValue(Series2BrushProperty); set => SetValue(Series2BrushProperty, value); }

    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }

    /// <summary>A fixed top of the scale (100 for a percentage); 0 = the largest value shown, rounded up.</summary>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public string Series1Name { get => GetValue(Series1NameProperty); set => SetValue(Series1NameProperty, value); }

    public string Series2Name { get => GetValue(Series2NameProperty); set => SetValue(Series2NameProperty, value); }

    public Func<double, string>? Format { get => GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    private double Top()
    {
        if (Maximum > 0) return Maximum;
        var max = Math.Max(Series1?.DefaultIfEmpty(0).Max() ?? 0, Series2?.DefaultIfEmpty(0).Max() ?? 0);
        if (max <= 0) return 1;
        // A round number above the data: 1, 2 or 5 times a power of ten.
        var power = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1.0, 2, 5, 10 })
            if (step * power >= max) return step * power;
        return 10 * power;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var plot = bounds.Deflate(new Thickness(0, 14, 0, 2));
        if (plot.Width <= 0 || plot.Height <= 0) return;
        var top = Top();
        var grid = new Pen(GridBrush ?? Brushes.Gray, 1);
        for (var i = 0; i <= 2; i++)
        {
            var y = Math.Round(plot.Bottom - plot.Height * i / 2) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        if (_maxText is null || _maxTextFor != top)
        {
            _maxText = Text(Format?.Invoke(top) ?? top.ToString("0", System.Globalization.CultureInfo.CurrentCulture), 11);
            _maxTextFor = top;
        }

        _maxText.Draw(context, new Point(plot.Right - _maxText.Width, 0));
        double X(int index) => plot.Right - (Capacity - 1 - index) * plot.Width / (Capacity - 1);
        double Y(double v) => plot.Bottom - Math.Clamp(v / top, 0, 1) * plot.Height;
        var offset = 0;
        void Line(IReadOnlyList<double>? series, IBrush? brush)
        {
            if (series is null || series.Count < 2 || brush is null) return;
            offset = Capacity - series.Count;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(offset), Y(series[0])), false);
                for (var i = 1; i < series.Count; i++) g.LineTo(new Point(X(offset + i), Y(series[i])));
                g.EndFigure(false);
            }

            context.DrawGeometry(null, new Pen(brush, 2, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), geometry);
        }

        Line(Series1, Series1Brush);
        Line(Series2, Series2Brush);

        if (_hover is not { } hover || Series1 is not { Count: > 0 } s1) return;
        var index = Math.Clamp(hover - (Capacity - s1.Count), 0, s1.Count - 1);
        var x = X(Capacity - s1.Count + index);
        context.DrawLine(new Pen(TextBrush ?? Brushes.Gray, 1, dashStyle: DashStyle.Dash), new Point(x, plot.Top), new Point(x, plot.Bottom));
        void Dot(IReadOnlyList<double>? series, IBrush? brush)
        {
            if (series is null || index >= series.Count || brush is null) return;
            var centre = new Point(x, Y(series[index]));
            context.DrawEllipse(SurfaceBrush, new Pen(SurfaceBrush, 2), centre, 6, 6);
            context.DrawEllipse(brush, null, centre, 4, 4);
        }

        Dot(Series1, Series1Brush);
        Dot(Series2, Series2Brush);
        _hoverText ??= Text(Readout(index, s1), 12);
        var box = new Rect(Math.Clamp(x - _hoverText.Width / 2 - 8, plot.Left, Math.Max(plot.Left, plot.Right - _hoverText.Width - 16)), plot.Top + 4, _hoverText.Width + 16, _hoverText.Height + 8);
        context.DrawRectangle(SurfaceBrush, new Pen(GridBrush, 1), box, 6, 6);
        _hoverText.Draw(context, new Point(box.Left + 8, box.Top + 4));
    }

    private string Readout(int index, IReadOnlyList<double> s1)
    {
        string F(double v) => Format?.Invoke(v) ?? v.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        var ago = s1.Count - 1 - index;
        var when = ago == 0 ? "now" : $"{ago} s ago";
        var first = Series1Name.Length > 0 ? $"{Series1Name} {F(s1[index])}" : F(s1[index]);
        var second = Series2 is { } s2 && index < s2.Count ? $" · {Series2Name} {F(s2[index])}" : "";
        return $"{first}{second}  ({when})";
    }

    private TextLayout Text(string text, double size) =>
        new(text, new Typeface(FontFamily.Default), size, TextBrush ?? Brushes.Gray);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        var index = (int)Math.Round((Capacity - 1) - (Bounds.Width - x) / Math.Max(1, Bounds.Width) * (Capacity - 1));
        index = Math.Clamp(index, 0, Capacity - 1);
        if (index == _hover) return;
        _hover = index;
        _hoverText = null;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        _hoverText = null;
        InvalidateVisual();
    }
}
