using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

[assembly: ExtensionApplication(typeof(MahodAI.Civil3D.Plugin.MahodRibbon))]

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Initializes the MahodAI Ribbon tab in Civil 3D.
    /// </summary>
    public class MahodRibbon : IExtensionApplication
    {
        private const string RIBBON_TAB_ID = "MahodAI_Tab";
        private const string RIBBON_TAB_TITLE = "MahodAI";

        public void Initialize()
        {
            // AutoCAD's plugin host doesn't probe the bundle's Contents/ folder for managed
            // dependencies (NetTopologySuite, DocumentFormat.OpenXml, PdfPig, …). Without an
            // AssemblyResolve hook the first JIT call into any tool that touches one of these
            // packages fails with FileNotFoundException, even though the DLL is right next to
            // MahodAI.Civil3D.Plugin.dll. Hook the resolver before anything else runs.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveBundleAssembly;

            // Subscribe to the Idle event to create ribbon after AutoCAD is ready
            Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle += OnApplicationIdle;
        }

        public void Terminate()
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveBundleAssembly;
        }

        private static readonly string _pluginDir =
            Path.GetDirectoryName(typeof(MahodRibbon).Assembly.Location) ?? string.Empty;

        private static Assembly? ResolveBundleAssembly(object? sender, ResolveEventArgs args)
        {
            try
            {
                if (string.IsNullOrEmpty(_pluginDir)) return null;
                var name = new AssemblyName(args.Name);
                if (string.IsNullOrEmpty(name.Name)) return null;
                // Skip resource assemblies — those follow a different lookup convention.
                if (name.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

                var candidate = Path.Combine(_pluginDir, name.Name + ".dll");
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            catch
            {
                // Swallow; let the default resolver take its course.
            }
            return null;
        }

        private void OnApplicationIdle(object? sender, EventArgs e)
        {
            // Unsubscribe immediately - we only need to run this once
            Autodesk.AutoCAD.ApplicationServices.Core.Application.Idle -= OnApplicationIdle;

            // Create the ribbon
            CreateRibbon();
        }

        private void CreateRibbon()
        {
            try
            {
                var ribbonControl = ComponentManager.Ribbon;
                if (ribbonControl == null)
                    return;

                // Check if tab already exists
                foreach (RibbonTab existingTab in ribbonControl.Tabs)
                {
                    if (existingTab.Id == RIBBON_TAB_ID)
                        return; // Already created
                }

                // Create the tab
                var tab = new RibbonTab
                {
                    Title = RIBBON_TAB_TITLE,
                    Id = RIBBON_TAB_ID
                };

                // Create the main panel
                var panel = new RibbonPanelSource
                {
                    Title = "AI Assistant"
                };

                // Create the main button - Open MahodAI
                var openButton = new RibbonButton
                {
                    Text = "Open\nMahodAI",
                    ShowText = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandHandler = new RibbonCommandHandler(),
                    ToolTip = "Open MahodAI Assistant Panel"
                };

                // Add button to panel
                panel.Items.Add(openButton);

                // Create panel and add to tab
                var ribbonPanel = new RibbonPanel
                {
                    Source = panel
                };
                tab.Panels.Add(ribbonPanel);

                // Civil Delivery: its own panel on the same tab, so the engineer never has
                // to type a command. Civil-only; harmless in plain AutoCAD (the command
                // itself refuses there with a message).
                var cdPanel = new RibbonPanelSource { Title = "Civil Delivery" };
                cdPanel.Items.Add(new RibbonButton
                {
                    Text = "Civil\nDelivery",
                    ShowText = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandHandler = new RibbonCommandHandler("MHD_CIVIL_DELIVERY"),
                    ToolTip = "Mahod Civil Delivery — חתכים לפי CL ואומדן מוקדם (MHD_CIVIL_DELIVERY)",
                });
                tab.Panels.Add(new RibbonPanel { Source = cdPanel });

                // Add tab to ribbon
                ribbonControl.Tabs.Add(tab);

                System.Diagnostics.Debug.WriteLine("MahodAI Ribbon tab created successfully");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating MahodAI Ribbon: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Handles ribbon button command execution.
    /// </summary>
    public class RibbonCommandHandler : System.Windows.Input.ICommand
    {
#pragma warning disable CS0067 // Required by ICommand interface
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        private readonly string? _command;

        /// <summary>Default: opens the MahodAI chat. With a command name: runs that command.</summary>
        public RibbonCommandHandler(string? command = null) => _command = command;

        public bool CanExecute(object? parameter)
        {
            return true;
        }

        public void Execute(object? parameter)
        {
            try
            {
                if (_command != null)
                {
                    // Route through the command pipeline (same path as typing it), so the
                    // palette, stage logs and document context behave identically.
                    var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                    doc?.SendStringToExecute(_command + " ", true, false, false);
                    return;
                }
                // Directly show the MahodAI window
                MahodChatWindow.Show();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error executing MahodAI command: {ex.Message}");
            }
        }
    }
}
