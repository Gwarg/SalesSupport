using System.Windows;
using System.Windows.Media;

namespace SalesSupport.Client;

/// <summary>
/// A five-segment audio level readout (the status strip's MIC/SPK meters). Segments light in
/// <see cref="OnBrush"/> as <see cref="Level"/> (0–100) passes each threshold; the rest stay
/// in <see cref="OffBrush"/>. Brushes come from the theme via DynamicResource.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    private static readonly double[] Thresholds = [4, 14, 30, 50, 72];
    private const double SegmentWidth = 3, Gap = 2, MaxHeight = 12;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OnBrushProperty = DependencyProperty.Register(
        nameof(OnBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OffBrushProperty = DependencyProperty.Register(
        nameof(OffBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Thresholds.Length * SegmentWidth + (Thresholds.Length - 1) * Gap, MaxHeight);

    protected override void OnRender(DrawingContext dc)
    {
        for (var i = 0; i < Thresholds.Length; i++)
        {
            var height = 4 + i * 2;
            var brush = Level >= Thresholds[i] ? OnBrush : OffBrush;
            dc.DrawRectangle(brush, null, new Rect(i * (SegmentWidth + Gap), MaxHeight - height, SegmentWidth, height));
        }
    }
}
