using System;
using System.Windows.Forms.Integration;
using Autodesk.AutoCAD.Windows;

namespace MahodAI.Civil3D.Plugin
{
    public class MahodChatWindow
    {
        private static PaletteSet _paletteSet = null!;
        private static NewChatControl _chatControl = null!;
        private static ElementHost _elementHost = null!;

        public static void Show()
        {
            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet(
                    "MahodAIChatWindow",
                    "Mahod AI Analyzer",
                    new Guid("A8F5E2D1-3C4B-4A5D-9E7F-1B2C3D4E5F6A"))
                {
                    MinimumSize = new System.Drawing.Size(420, 600),
                    Size = new System.Drawing.Size(480, 750),
                    DockEnabled = DockSides.Left | DockSides.Right,
                    Style = PaletteSetStyles.ShowCloseButton |
                            PaletteSetStyles.ShowAutoHideButton |
                            PaletteSetStyles.Snappable
                };

                _chatControl = new NewChatControl();

                _elementHost = new ElementHost
                {
                    Dock = System.Windows.Forms.DockStyle.Fill,
                    Child = _chatControl
                };

                _paletteSet.Add("Analyzer", _elementHost);
            }

            _paletteSet.Visible = true;
        }

        public static void Hide()
        {
            if (_paletteSet != null)
                _paletteSet.Visible = false;
        }

        public static void Toggle()
        {
            if (_paletteSet == null || !_paletteSet.Visible)
                Show();
            else
                Hide();
        }
    }
}