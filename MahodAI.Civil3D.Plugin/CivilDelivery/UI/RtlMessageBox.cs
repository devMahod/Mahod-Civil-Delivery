namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// Every palette message is Hebrew, often with a layer name, a rule key or a path inside it. The plain
    /// four-argument <c>System.Windows.MessageBox.Show</c> lays it out left-to-right, which scrambled the reason in
    /// the restore prompt on the b19 live run (02.10). This shows the same message right-to-left and right-aligned;
    /// the default button stays the first one, as before. A message that carries a path goes through
    /// <see cref="ShowPath(string?, string, string?, string, System.Windows.MessageBoxButton, System.Windows.MessageBoxImage, string?)"/>.
    /// WPF types are spelled out: the plugin also imports System.Windows.Forms.
    /// </summary>
    internal static class RtlMessageBox
    {
        public const System.Windows.MessageBoxOptions Options =
            System.Windows.MessageBoxOptions.RtlReading | System.Windows.MessageBoxOptions.RightAlign;

        public static System.Windows.MessageBoxResult Show(
            string text, string caption, System.Windows.MessageBoxButton buttons, System.Windows.MessageBoxImage icon) =>
            System.Windows.MessageBox.Show(text, caption, buttons, icon, System.Windows.MessageBoxResult.None, Options);

        /// <summary>
        /// A palette message that carries a file or folder path (b23). The Win32 message box wrapped a long path by its own
        /// width and laid it out right-to-left row by row — on b21 and, with b22's embedding marks, again on the b22 live
        /// run 08:19 ("C:" alone, "-Users\…" on the next row). The Hebrew <paramref name="prefix"/> and
        /// <paramref name="suffix"/> stay right-to-left; <paramref name="rawPath"/> is shown unchanged in its own
        /// left-to-right field (<see cref="PathMessageDialog"/>), hosted modally by Civil like the other palette dialogs.
        /// <paramref name="displayName"/> is an optional file name shown for recognition only. Returns Yes only when Yes
        /// was chosen; the caller alone opens the file or folder, with the raw path.
        /// </summary>
        public static System.Windows.MessageBoxResult ShowPath(string? prefix, string rawPath, string? suffix, string caption,
            System.Windows.MessageBoxButton buttons, System.Windows.MessageBoxImage icon, string? displayName = null) =>
            ShowPath(new PathMessageDialog(prefix, rawPath, suffix, caption, buttons, icon, displayName),
                dialog => CivilModalHost.ShowFromPalette(dialog));

        /// <summary>Test seam: the answer is the dialog's own result, whatever the host returns.</summary>
        internal static System.Windows.MessageBoxResult ShowPath(PathMessageDialog dialog, System.Func<System.Windows.Window, bool?> show)
        {
            show(dialog);
            return dialog.Result;
        }
    }
}
