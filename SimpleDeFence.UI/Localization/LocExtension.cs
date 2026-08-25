using Microsoft.UI.Xaml.Markup;
using SimpleDeFence.Localization;

namespace SimpleDeFence.UI.Localization
{
    /// <summary>
    /// Lets XAML pull from the same string catalog as code-behind: <c>Text="{loc:Loc Key=nav.rules}"</c>
    /// instead of a literal. Resolved once, at parse time - like {StaticResource} - which is enough
    /// for chrome that does not need to change without a restart. Live-updating text (the mode chip,
    /// dialogs) goes through bound view-model properties that call Loc.T directly instead.
    /// </summary>
    [MarkupExtensionReturnType(ReturnType = typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public string Key { get; set; } = string.Empty;

        protected override object ProvideValue() => Loc.T(Key);
    }
}
