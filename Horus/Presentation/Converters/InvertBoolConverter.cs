using System.Globalization;

namespace Horus.Presentation.Converters
{
    public class InvertBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is bool b && !b;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is bool b && !b;
    }

    public class BoolToEyeConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? "🙈" : "👁";

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class BoolToColorConverter : IValueConverter
    {
        public Color? TrueColor { get; set; }
        public Color? FalseColor { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? TrueColor : FalseColor;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Dims a control when a bool is false — mirrors the mock's disabled-button opacity.
    /// TrueOpacity / FalseOpacity default to 1.0 / 0.45.
    /// </summary>
    public class BoolToOpacityConverter : IValueConverter
    {
        public double TrueOpacity { get; set; } = 1.0;
        public double FalseOpacity { get; set; } = 0.45;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? TrueOpacity : FalseOpacity;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    public class VpnStateToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Domain.Models.VpnState state)
            {
                return state switch
                {
                    Domain.Models.VpnState.Connected => Color.FromArgb("#57CC97"),
                    Domain.Models.VpnState.Connecting => Color.FromArgb("#E0A52E"),
                    Domain.Models.VpnState.Reconnecting => Color.FromArgb("#E0A52E"),
                    Domain.Models.VpnState.Error => Color.FromArgb("#E15668"),
                    _ => Color.FromArgb("#E15668")
                };
            }
            return Color.FromArgb("#E15668");
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Converts an enum value to its int index and back.
    /// Used for binding Picker.SelectedIndex to an enum property.
    /// Parameter (optional): comma-separated list of enum value names in picker order,
    ///   e.g. "Disabled,Whitelist,Blacklist". If omitted, uses the numeric enum value directly.
    /// </summary>
    public class EnumToIntConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return 0;
            if (parameter is string order)
            {
                var names = order.Split(',');
                var name = value.ToString() ?? string.Empty;
                var idx = Array.IndexOf(names, name);
                return idx >= 0 ? idx : 0;
            }
            return (int)value;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not int idx) return Activator.CreateInstance(targetType);
            if (parameter is string order)
            {
                var names = order.Split(',');
                if (idx >= 0 && idx < names.Length)
                    return Enum.Parse(targetType, names[idx]);
            }
            return Enum.ToObject(targetType, idx);
        }
    }

    /// <summary>
    /// Returns true when the string is non-null and non-empty.
    /// Useful for IsVisible bindings on status labels.
    /// </summary>
    public class StringToBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            !string.IsNullOrEmpty(value as string);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Converts bool to one of two strings.
    /// TrueText / FalseText properties; defaults "Yes" / "No".
    /// Used for "Add Server" / "Edit Server" label.
    /// </summary>
    public class BoolToStringConverter : IValueConverter
    {
        public string TrueText { get; set; } = "ADD SERVER";
        public string FalseText { get; set; } = "EDIT SERVER";

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? TrueText : FalseText;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
