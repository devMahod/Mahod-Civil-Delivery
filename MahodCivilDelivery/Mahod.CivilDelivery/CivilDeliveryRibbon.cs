using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using Autodesk.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Commands;
using MahodAI.Civil3D.Plugin.Utilities;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin;

/// <summary>
/// Puts the Civil Delivery button on the Mahod ribbon tab that MahodAI creates, and hides MahodAI's own button for
/// the older Civil Delivery it still carries.
/// </summary>
/// <remarks>
/// MahodAI builds its tab on its first Idle and gives up if a tab with its id already exists — so this plugin must
/// never create "MahodAI_Tab" itself, or MahodAI would lose every button. It joins that tab whenever it appears (before
/// or after us, and again if the ribbon is rebuilt). Without MahodAI it opens its own tab under a different id, and
/// moves the panel into MahodAI's tab if that appears later.
/// </remarks>
internal static class CivilDeliveryRibbon
{
    internal const string MahodTabId = "MahodAI_Tab";
    internal const string OwnTabId = "MahodCivilDelivery_Tab";
    internal const string PanelId = "MahodCivilDelivery_Panel";
    internal const string OldCommand = "MHD_CIVIL_DELIVERY";

    private static bool _subscribed;
    private static bool _placePending;

    public static void Install() => QueuePlace();

    private static void QueuePlace()
    {
        if (_placePending) return;
        _placePending = true;
        AcadApp.Idle += OnIdle;
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        AcadApp.Idle -= OnIdle;
        _placePending = false;
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
            {
                // The ribbon is created after start-up; try again when it exists.
                ComponentManager.ItemInitialized -= OnItemInitialized;
                ComponentManager.ItemInitialized += OnItemInitialized;
                return;
            }
            if (!_subscribed && ribbon.Tabs is INotifyCollectionChanged tabs)
            {
                tabs.CollectionChanged += (_, _) => QueuePlace(); // never edit the collection inside its own event
                _subscribed = true;
            }
            Place(ribbon);
        }
        catch (Exception ex)
        {
            try { MahodLogger.Warning("Civil Delivery ribbon: " + ex.Message); } catch { }
        }
    }

    private static void OnItemInitialized(object? sender, RibbonItemEventArgs e)
    {
        if (ComponentManager.Ribbon == null) return;
        ComponentManager.ItemInitialized -= OnItemInitialized;
        QueuePlace();
    }

    /// <summary>One idempotent pass: our panel on MahodAI's tab if it exists, else on our own tab.</summary>
    internal static void Place(RibbonControl ribbon)
    {
        var mahod = ribbon.Tabs.FirstOrDefault(t => t.Id == MahodTabId);
        var own = ribbon.Tabs.FirstOrDefault(t => t.Id == OwnTabId);
        if (mahod != null)
        {
            if (own != null) ribbon.Tabs.Remove(own);
            if (!mahod.Panels.Any(p => p.Source?.Id == PanelId)) mahod.Panels.Add(CreatePanel());
            HideOldButton(mahod);
            return;
        }
        if (own == null)
        {
            own = new RibbonTab { Id = OwnTabId, Title = "Mahod" };
            own.Panels.Add(CreatePanel());
            ribbon.Tabs.Add(own);
        }
    }

    /// <summary>
    /// MahodAI's button for its older Civil Delivery (MHD_CIVIL_DELIVERY): hidden, never removed. A panel left with no
    /// visible item is hidden too — live 29.09.2026 its title ("חתכים וכמויות") stayed on the tab over an empty slot.
    /// </summary>
    private static void HideOldButton(RibbonTab tab)
    {
        foreach (var panel in tab.Panels)
        {
            if (panel.Source == null || panel.Source.Id == PanelId) continue;
            var hidOne = false;
            foreach (var item in panel.Source.Items.OfType<RibbonButton>())
            {
                var tip = item.ToolTip?.ToString() ?? "";
                var text = (item.Text ?? "").Replace("\n", " ").Trim();
                if (tip.Contains("(" + OldCommand + ")", StringComparison.Ordinal) ||
                    string.Equals(text, "Civil Delivery", StringComparison.OrdinalIgnoreCase))
                {
                    item.IsVisible = false;
                    hidOne = true;
                }
            }
            if (hidOne && !panel.Source.Items.Any(i => i.IsVisible)) panel.IsVisible = false;
        }
    }

    /// <summary>The button icon embedded in this plugin; null leaves the text-only button rather than failing the ribbon.</summary>
    private static System.Windows.Media.ImageSource? Icon(string name)
    {
        try
        {
            using var stream = typeof(CivilDeliveryRibbon).Assembly.GetManifestResourceStream("Mahod.CivilDelivery.assets." + name);
            if (stream == null) return null;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            try { MahodLogger.Warning("Civil Delivery ribbon icon " + name + ": " + ex.Message); } catch { }
            return null;
        }
    }

    private static RibbonPanel CreatePanel()
    {
        var source = new RibbonPanelSource { Id = PanelId, Title = "Civil Delivery" };
        source.Items.Add(new RibbonButton
        {
            Id = PanelId + "_Open",
            Text = "Civil\nDelivery",
            ShowText = true,
            ShowImage = true,
            LargeImage = Icon("civil_delivery_32.png"),
            Image = Icon("civil_delivery_16.png"),
            Size = RibbonItemSize.Large,
            Orientation = System.Windows.Controls.Orientation.Vertical,
            ToolTip = $"Mahod Civil Delivery — חתכים לפי CL וכתב כמויות מהשרטוט ({CivilDeliveryCommandNames.Panel})",
            CommandHandler = new SendCommand(CivilDeliveryCommandNames.Panel),
        });
        return new RibbonPanel { Source = source };
    }

    private sealed class SendCommand : ICommand
    {
        private readonly string _command;
        public SendCommand(string command) => _command = command;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) =>
            AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute(_command + " ", true, false, false);
    }
}
