using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Orbit.Views;

/// <summary>A slider with a number box next to it (like Fishstrap's quality slider).</summary>
public sealed class NumberSlider : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumberSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumberSlider), new PropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumberSlider), new PropertyMetadata(10.0, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumberSlider), new PropertyMetadata(1.0, OnRangeChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public int IntValue => (int)Math.Round(Value);

    public event EventHandler? ValueChanged;

    private readonly Slider _slider;
    private readonly TextBox _box;
    private bool _syncing;

    public NumberSlider()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        VerticalAlignment = VerticalAlignment.Center;

        _slider = new Slider { Width = 220, Margin = new Thickness(0, 0, 12, 0) };
        _box = new TextBox { Style = (Style)Application.Current.FindResource("NumberBox") };
        SetColumn(_box, 1);
        Children.Add(_slider);
        Children.Add(_box);

        _slider.ValueChanged += (_, e) =>
        {
            if (!_syncing) Value = Snap(e.NewValue);
        };
        _box.LostKeyboardFocus += (_, _) => CommitBox();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitBox();
        };

        Sync();
    }

    private double Snap(double v)
    {
        var step = Step <= 0 ? 1 : Step;
        var snapped = Math.Round((v - Minimum) / step) * step + Minimum;
        return Math.Round(Math.Clamp(snapped, Minimum, Maximum), 4);
    }

    private void CommitBox()
    {
        if (double.TryParse(_box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            Value = Snap(v);
        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        _slider.Minimum = Minimum;
        _slider.Maximum = Maximum;
        _slider.TickFrequency = Step <= 0 ? 1 : Step;
        _slider.SmallChange = _slider.TickFrequency;
        _slider.LargeChange = _slider.TickFrequency;
        _slider.Value = Value;
        _box.Text = Step < 1 ? Value.ToString("0.0#", CultureInfo.InvariantCulture) : ((int)Math.Round(Value)).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (NumberSlider)d;
        self.Sync();
        self.ValueChanged?.Invoke(self, EventArgs.Empty);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((NumberSlider)d).Sync();
}

/// <summary>Attached property that limits a TextBox to whole numbers.</summary>
public static class Numeric
{
    public static readonly DependencyProperty IntegerOnlyProperty = DependencyProperty.RegisterAttached(
        "IntegerOnly", typeof(bool), typeof(Numeric), new PropertyMetadata(false, OnChanged));

    public static bool GetIntegerOnly(DependencyObject o) => (bool)o.GetValue(IntegerOnlyProperty);
    public static void SetIntegerOnly(DependencyObject o, bool value) => o.SetValue(IntegerOnlyProperty, value);

    private static readonly Regex Digits = new("^[0-9]+$");

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || e.NewValue is not true) return;
        box.PreviewTextInput += (_, args) => args.Handled = !Digits.IsMatch(args.Text);
        DataObject.AddPastingHandler(box, (_, args) =>
        {
            var text = args.DataObject.GetData(typeof(string)) as string;
            if (text == null || !Digits.IsMatch(text.Trim())) args.CancelCommand();
        });
    }

    public static int Parse(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

/// <summary>An entry in a dropdown.</summary>
public sealed record Option(string Label, object Value)
{
    public override string ToString() => Label;

    public static void Select(ComboBox combo, object value)
    {
        var items = ((IEnumerable<Option>)combo.ItemsSource).ToList();
        combo.SelectedItem = items.FirstOrDefault(o => Equals(o.Value, value)) ?? items.First();
    }

    public static T Get<T>(ComboBox combo, T fallback)
        => combo.SelectedItem is Option { Value: T value } ? value : fallback;
}
