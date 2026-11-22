using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P1.C1.Controls.Sliders;
using P1.C1.WPF.Styles;

internal sealed class NativeSlider : Grid
{
    private static readonly Uri StyleSource = new("/CaptureOne;component/WPF/Styles/DefaultStyle.xaml", UriKind.Relative);
    private readonly ToolSliderWPF control;
    private readonly decimal displayScale;
    private readonly double defaultValue;

    internal NativeSlider(string title, double minimum, double maximum, double initial, double step,
        string unit = "", double displayScale = 1, int? decimalPlaces = null)
    {
        this.displayScale = (decimal)displayScale;
        defaultValue = initial;
        Margin = new Thickness(0, 2, 0, 2);
        Resources.MergedDictionaries.Add(new SharedResourceDictionary { Source = StyleSource });

        var increment = (decimal)step * this.displayScale;
        var places = decimalPlaces ?? DecimalPlacesFor(increment);
        control = new ToolSliderWPF
        {
            LabelText = title,
            Minimum = ToDisplay(minimum),
            Maximum = ToDisplay(maximum),
            Increment = increment,
            DecimalPlaces = places,
            Unit = unit,
            Value = ToDisplay(initial),
        };
        // The standard box is 40 DIPs. Very small optical settings need more space.
        if (places > 4)
            control.UpDownWidth += (places - 4) * 8;
        AutomationProperties.SetName(control, title);
        Children.Add(control);
        control.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ToolSliderWPF.Value))
                ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        control.SliderPointerDown += (_, _) => DragStarted?.Invoke(this, EventArgs.Empty);
        control.SliderPointerUp += (_, _) => DragCompleted?.Invoke(this, EventArgs.Empty);
        control.SliderMouseDoubleClick += (_, args) =>
        {
            ResetToDefault();
            args.Handled = true;
        };
        PreviewMouseLeftButtonDown += ResetOnDoubleClick;
        Titles.Add(title);
        baseLabelWidth = control.LabelWidth;
        Loaded += OnLoaded;
        Unloaded += (_, _) => WidestLabelChanged -= FitLabel;
        SizeChanged += (_, _) => FitLabel();
    }

    // Label column (2026-11-21): Capture One's slider has a fixed label width, so longer names were cut
    // ("Scanner MT…"). All panel sliders share one column wide enough for the longest name, measured with the
    // label's own font, as long as the slider track keeps at least half of the room next to the number box
    // (and never less than MinimumTrack); in a narrow panel long names are cut again.
    private const double MinimumTrack = 110;
    // A set: temporary panels built for batch exports add the same names again.
    private static readonly System.Collections.Generic.HashSet<string> Titles = new();
    private static double widestLabel;
    private static int measuredTitles;
    private static event Action? WidestLabelChanged;
    private readonly float baseLabelWidth;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        WidestLabelChanged -= FitLabel;
        WidestLabelChanged += FitLabel;
        control.ApplyTemplate();
        if (measuredTitles != Titles.Count && control.Template?.FindName("PART_Label", control) is TextBlock label)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
            double TextWidth(string text) => new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                label.FlowDirection, typeface, label.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
            // Whatever the label template adds around the text (margin, padding), taken from this label.
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var extra = Math.Max(0, label.DesiredSize.Width - TextWidth(label.Text));
            var widest = Titles.Max(TextWidth) + extra + 2;
            measuredTitles = Titles.Count;
            if (widest > widestLabel)
            {
                widestLabel = widest;
                WidestLabelChanged?.Invoke();
            }
        }
        FitLabel();
    }

    private void FitLabel()
    {
        if (ActualWidth <= 0)
            return;
        var available = ActualWidth - control.UpDownWidth;
        var room = available - Math.Max(MinimumTrack, available / 2);
        var width = (float)Math.Max(baseLabelWidth, Math.Min(widestLabel, room));
        if (Math.Abs(control.LabelWidth - width) > 0.5f)
            control.LabelWidth = width;
    }

    internal double Value
    {
        get => (double)(control.Value / displayScale);
        set => control.Value = ToDisplay(Math.Clamp(value, Minimum, Maximum));
    }

    internal double Minimum => (double)(control.Minimum / displayScale);
    internal double Maximum => (double)(control.Maximum / displayScale);

    internal event EventHandler? ValueChanged;
    internal event EventHandler? DragStarted;
    internal event EventHandler? DragCompleted;

    internal void ResetToDefault() => Value = defaultValue;

    private decimal ToDisplay(double value) => (decimal)value * displayScale;

    private static int DecimalPlacesFor(decimal increment)
    {
        var places = 0;
        while (decimal.Truncate(increment) != increment)
        {
            increment *= 10;
            places++;
        }
        return places;
    }

    private void ResetOnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount != 2 || args.OriginalSource is not Visual source || control.Template is not { } template)
            return;

        if (IsWithin(source, template.FindName("PART_Label", control))
            || IsWithin(source, template.FindName("PART_Slider", control)))
        {
            ResetToDefault();
            args.Handled = true;
        }
    }

    private static bool IsWithin(Visual source, object? part) =>
        part is Visual visual && (visual == source || visual.IsAncestorOf(source));
}
