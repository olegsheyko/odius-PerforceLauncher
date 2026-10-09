using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PerforceLauncher.App.Infrastructure;

/// <summary>
/// Серая подсказка в пустом TextBox (в WPF нет PlaceholderText): &lt;TextBox inf:Hint.Text="..."/&gt;.
/// Рисуется adorner'ом поверх поля и не мешает вводу.
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string? value) => o.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        if (box.IsLoaded) Attach(box);
        else box.Loaded += (_, _) => Attach(box);
    }

    private static void Attach(TextBox box)
    {
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer == null) return;
        var existing = layer.GetAdorners(box)?.OfType<HintAdorner>().FirstOrDefault();
        if (existing != null) { existing.InvalidateVisual(); return; }
        var adorner = new HintAdorner(box);
        layer.Add(adorner);
        box.TextChanged += (_, _) => adorner.InvalidateVisual();
        box.SizeChanged += (_, _) => adorner.InvalidateVisual();
    }

    private sealed class HintAdorner : Adorner
    {
        private static readonly Brush HintBrush = Palette.Frozen(0xFF7A7A85);

        public HintAdorner(TextBox box) : base(box) => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext dc)
        {
            var box = (TextBox)AdornedElement;
            var text = GetText(box);
            if (!string.IsNullOrEmpty(box.Text) || string.IsNullOrEmpty(text) || !box.IsVisible) return;

            var left = box.Padding.Left + box.BorderThickness.Left + 2;
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                box.FontSize, HintBrush, VisualTreeHelper.GetDpi(box).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, box.ActualWidth - left - 10),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(ft, new Point(left, Math.Max(0, (box.ActualHeight - ft.Height) / 2)));
        }
    }
}
