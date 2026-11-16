using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Differ.UI.Gallery;

public sealed class SampleQuantityRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo)
    {
        return int.TryParse(value as string, NumberStyles.Integer, cultureInfo, out int quantity) && quantity is >= 1 and <= 99
            ? ValidationResult.ValidResult
            : new ValidationResult(false, Application.Current.FindResource("Gallery.QuantityError"));
    }
}
