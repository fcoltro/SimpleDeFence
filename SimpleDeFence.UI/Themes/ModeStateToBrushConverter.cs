using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace SimpleDeFence.UI.Themes
{
    /// <summary>Maps ShellViewModel.ModeStateKey to one of the status brushes.</summary>
    public sealed class ModeStateToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var key = value as string ?? "Neutral";
            var resourceKey = key switch
            {
                "Success" => "StatusSuccessBrush",
                "Caution" => "StatusCautionBrush",
                "Information" => "StatusInformationBrush",
                "AccentAlt" => "StatusAccentAltBrush",
                _ => "StatusNeutralBrush",
            };

            if (Application.Current.Resources.TryGetValue(resourceKey, out var brush))
                return brush;

            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
