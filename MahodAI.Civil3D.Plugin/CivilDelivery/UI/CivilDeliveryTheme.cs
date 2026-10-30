using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The Mahod house look (TenderOS "clean" skin) for the windows this tool builds in code: the same tokens as
/// <c>CivilDeliveryTheme.xaml</c>, which <see cref="Apply"/> merges into the window so its buttons, inputs,
/// tables and expanders take the shared styles. Light grey canvas, white rounded cards, one ink colour, Rubik
/// (Segoe UI where Rubik is not installed), green only where it carries meaning.
/// </summary>
internal static class CivilDeliveryTheme
{
    /// <summary>
    /// Rubik, shipped as files in the Fonts folder beside this assembly (source: CivilDelivery/UI/Fonts, OFL licence),
    /// so every engineer sees it without installing anything. WPF loads fonts from a folder on disk inside any host;
    /// loading them from a plugin's own resources does not work under a host executable such as acad.exe. Segoe UI
    /// and Arial stand in where a build left the folder out. Every window sets this after InitializeComponent, because
    /// its XAML value ("./Fonts/#Rubik") only resolves where the markup is parsed from the source folder.
    /// </summary>
    public static readonly FontFamily Font = LoadFont();

    /// <summary>The Fonts folder beside this assembly, or null when the build did not ship it.</summary>
    public static string? FontsFolder
    {
        get
        {
            try
            {
                var location = typeof(CivilDeliveryTheme).Assembly.Location;
                if (string.IsNullOrEmpty(location)) return null;
                var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(location)!, "Fonts");
                return System.IO.Directory.Exists(folder) ? folder : null;
            }
            catch { return null; }
        }
    }

    private static FontFamily LoadFont()
    {
        try
        {
            if (FontsFolder is { } folder)
                return new FontFamily(new Uri(folder + System.IO.Path.DirectorySeparatorChar), "./#Rubik, Segoe UI, Arial");
        }
        catch { /* the fallback chain below */ }
        return new FontFamily("Rubik, Segoe UI, Arial");
    }

    public static readonly Color CanvasColor = Color.FromRgb(0xF0, 0xF2, 0xF5);
    public static readonly Color PanelColor = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color InkColor = Color.FromRgb(0x1C, 0x24, 0x20);
    public static readonly Color MutedColor = Color.FromRgb(0x6B, 0x75, 0x70);
    public static readonly Color AccentColor = Color.FromRgb(0x1C, 0x9A, 0x5B);

    /// <summary>The page: light grey.</summary>
    public static readonly Brush Canvas = Frozen(0xF0, 0xF2, 0xF5);
    /// <summary>A window on the page: white.</summary>
    public static readonly Brush Panel = Frozen(0xFF, 0xFF, 0xFF);
    /// <summary>A quieter surface inside a white window (read-only text, headers).</summary>
    public static readonly Brush Panel2 = Frozen(0xF6, 0xF7, 0xF9);
    /// <summary>All text and icons.</summary>
    public static readonly Brush Ink = Frozen(0x1C, 0x24, 0x20);
    /// <summary>Secondary text that must still read on a tinted row.</summary>
    public static readonly Brush TextSoft = Frozen(0x55, 0x60, 0x5B);
    /// <summary>Grey meta text.</summary>
    public static readonly Brush Muted = Frozen(0x6B, 0x75, 0x70);
    /// <summary>Hairlines inside a window.</summary>
    public static readonly Brush Rule = Frozen(0xED, 0xF0, 0xF2);
    /// <summary>The outline of an input or a plain button.</summary>
    public static readonly Brush Frame = Frozen(0xE1, 0xE5, 0xEA);
    public static readonly Brush FrameStrong = Frozen(0xC3, 0xCA, 0xD2);
    public static readonly Brush Hover = Frozen(0xF3, 0xF7, 0xF4);
    /// <summary>The one accent: where you are, what is good.</summary>
    public static readonly Brush Accent = Frozen(0x1C, 0x9A, 0x5B);
    public static readonly Brush AccentTint = Frozen(0xE5, 0xF6, 0xEC);
    public static readonly Brush AccentInk = Frozen(0x0F, 0x5D, 0x3A);
    public static readonly Brush Ok = Accent;
    public static readonly Brush Review = Frozen(0xB8, 0x70, 0x0F);
    public static readonly Brush ReviewTint = Frozen(0xFB, 0xF1, 0xE1);
    public static readonly Brush Warn = Review;
    public static readonly Brush Blocked = Frozen(0xC2, 0x41, 0x3B);
    public static readonly Brush BlockedTint = Frozen(0xFB, 0xE9, 0xE8);
    public static readonly Brush Selection = AccentTint;

    private static ResourceDictionary? _dictionary;

    /// <summary>The shared styles, loaded once from this assembly (whichever assembly compiled the tool).</summary>
    public static ResourceDictionary Dictionary => _dictionary ??= Load();

    private static ResourceDictionary Load()
    {
        var assembly = typeof(CivilDeliveryTheme).Assembly.GetName().Name;
        var path =
#if MAHOD_CD_STANDALONE
            "CivilDelivery/UI/CivilDeliveryTheme.xaml";
#else
            "Ported/CivilDelivery/UI/CivilDeliveryTheme.xaml";
#endif
        var uri = new Uri($"/{assembly};component/{path}", UriKind.Relative);
        return (ResourceDictionary)System.Windows.Application.LoadComponent(uri);
    }

    /// <summary>
    /// Gives a window built in code the house look: canvas background, ink text, the font, and the shared styles
    /// for every control it holds. A missing dictionary never stops a dialog; the window keeps its plain colours.
    /// </summary>
    public static void Apply(Window window, double fontSize = 13)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.FontFamily = Font;
        window.FontSize = fontSize;
        window.Background = Canvas;
        window.Foreground = Ink;
        window.UseLayoutRounding = true;
        try
        {
            var shared = Dictionary;
            if (!window.Resources.MergedDictionaries.Contains(shared))
                window.Resources.MergedDictionaries.Insert(0, shared);
        }
        catch (Exception ex)
        {
            try { MahodAI.Civil3D.Plugin.Utilities.MahodLogger.Warning("Civil Delivery theme: " + ex.Message); } catch { }
        }
    }

    /// <summary>A white rounded window on the canvas, holding one element.</summary>
    public static Border Card(UIElement child, double padding = 12) => new()
    {
        Background = Panel,
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(padding),
        Child = child,
        SnapsToDevicePixels = true,
    };

    /// <summary>A quieter grey window inside a white one.</summary>
    public static Border InsetCard(UIElement child, double padding = 10) => new()
    {
        Background = Panel2,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(padding),
        Child = child,
    };

    /// <summary>The soft amber notice box: a line the engineer must read before deciding.</summary>
    public static Border Notice(UIElement child) => new()
    {
        Background = ReviewTint,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 9, 12, 9),
        Child = child,
    };

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
