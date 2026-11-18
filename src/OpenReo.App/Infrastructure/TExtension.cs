using System.Windows.Markup;
using OpenReo.Core.Localization;

namespace OpenReo.App.Infrastructure;

/// <summary>
/// XAML markup extension for translated texts: <c>Text="{l:T 'Kamera hinzufügen'}"</c>.
/// The language is fixed at startup, so the value is resolved once.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) { Text = text; }

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Lang.T(Text);
}
