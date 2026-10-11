using System.Windows;
using System.Windows.Controls;

namespace IPTVPlayer.Views.Controls;

/// <summary>
/// Painel que posiciona os filhos na horizontal em frações da sua largura
/// (TimelinePanel.Start = 0..1, TimelinePanel.Length = 0..1). Usado na grelha do EPG:
/// redimensionar a janela reescala tudo sem recalcular os programas.
/// Length = 0 usa a largura desejada do filho (marcadores de hora).
/// </summary>
public sealed class TimelinePanel : Panel
{
    public static readonly DependencyProperty StartProperty = DependencyProperty.RegisterAttached(
        "Start", typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty LengthProperty = DependencyProperty.RegisterAttached(
        "Length", typeof(double), typeof(TimelinePanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetStart(DependencyObject d) => (double)d.GetValue(StartProperty);
    public static void SetStart(DependencyObject d, double value) => d.SetValue(StartProperty, value);
    public static double GetLength(DependencyObject d) => (double)d.GetValue(LengthProperty);
    public static void SetLength(DependencyObject d, double value) => d.SetValue(LengthProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var length = GetLength(child);
            var childWidth = length > 0 && width > 0 ? length * width : double.PositiveInfinity;
            child.Measure(new Size(childWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        foreach (UIElement child in InternalChildren)
        {
            var x = GetStart(child) * width;
            var length = GetLength(child);
            var w = length > 0 ? length * width : child.DesiredSize.Width;
            if (x < 0)
            {
                w += x;
                x = 0;
            }
            w = Math.Max(0, Math.Min(w, width - x));
            child.Arrange(new Rect(x, 0, w, finalSize.Height));
        }
        return finalSize;
    }
}
