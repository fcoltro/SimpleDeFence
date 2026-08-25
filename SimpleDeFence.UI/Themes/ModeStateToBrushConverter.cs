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

            // Even the defensive path stays theme-aware. A hardcoded colour here would survive
            // into high-contrast themes as a fixed grey, which is precisely what binding these
            // to system semantic brushes exists to prevent.
            if (Application.Current.Resources.TryGetValue("SystemFillColorNeutralBrush", out var systemNeutral))
                return systemNeutral;

            // Nothing resolvable: absence of colour rather than a wrong colour. The icon and
            // word still carry the status, since status is never signalled by colour alone.
            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
