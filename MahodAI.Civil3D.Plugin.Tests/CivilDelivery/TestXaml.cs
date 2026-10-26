using System.IO;
using System.Text;
using System.Windows.Markup;
using System.Xml.Linq;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// Parses production markup the way the offline layout tests need it: the XAML text (already stripped of its class
/// and handlers) with the UI source folder as its base URI, so the shared theme that every window merges by a
/// relative Source ("CivilDeliveryTheme.xaml", 2026-10) loads from the same folder as loose XAML. Without a base
/// URI WPF would look for the theme in the test host's own resources and find nothing.
/// </summary>
internal static class TestXaml
{
    public static readonly string UiDirectory = Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI");

    public static object Parse(XDocument markup)
    {
        var context = new ParserContext { BaseUri = new Uri(Path.Combine(UiDirectory, "markup.xaml")) };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markup.ToString()));
        return XamlReader.Load(stream, context);
    }
}
