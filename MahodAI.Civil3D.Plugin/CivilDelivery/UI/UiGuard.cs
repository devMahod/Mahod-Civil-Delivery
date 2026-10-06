using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    /// <summary>
    /// A recoverable WPF error must not escape into AutoCAD (live on 6422,
    /// 2026-09-03: a DataGrid refresh during an edit transaction crashed the host).
    /// Explicit handlers use <see cref="Run"/>; loaded dialogs also contain errors
    /// attributable to this product's UI. Fatal and unrelated exceptions remain
    /// under the host's control; containment alone proves neither success nor rollback.
    /// </summary>
    internal static class UiGuard
    {
        public static string ErrorLogPath => Path.Combine(StageLog.DefaultDirectory, "ui_errors.log");

        /// <summary>Reports recoverable UI failures; fatal runtime failures are not suppressed.</summary>
        public static void Run(string context, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                Report(context, ex);
            }
        }

        /// <summary>
        /// Guards recoverable exceptions originating in our UI while this window is loaded.
        /// Attaching an unfinished constructor must not subscribe to AutoCAD's shared
        /// dispatcher: that window may never be shown or receive Closed.
        /// </summary>
        public static void Attach(Window window, string context)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            var dispatcher = window.Dispatcher;
            var registered = false;
            DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
            {
                if (e.Handled || !ShouldHandleDispatcherException(e.Exception)) return;
                e.Handled = true;
                Report(context, e.Exception);
            };
            void Register()
            {
                if (registered) return;
                dispatcher.UnhandledException += handler;
                registered = true;
            }
            void Unregister()
            {
                if (!registered) return;
                dispatcher.UnhandledException -= handler;
                registered = false;
            }
            window.Loaded += (_, _) => Register();
            window.Unloaded += (_, _) => Unregister();
            window.Closed += (_, _) => Unregister();
            if (window.IsLoaded) Register();
        }

        internal static bool IsRecoverable(Exception exception)
        {
            if (exception is OutOfMemoryException or StackOverflowException or
                AccessViolationException or AppDomainUnloadedException or
                BadImageFormatException or SEHException) return false;
            if (exception is AggregateException aggregate &&
                aggregate.InnerExceptions.Any(inner => !IsRecoverable(inner))) return false;
            return exception.InnerException == null || IsRecoverable(exception.InnerException);
        }

        /// <summary>
        /// Dispatcher ownership is not window ownership. An unrelated add-in can post
        /// work during a modal dialog; only a real frame from our UI is attributable to us.
        /// Unknown WPF-only failures and fatal runtime failures stay with the host.
        /// </summary>
        internal static bool ShouldHandleDispatcherException(Exception exception)
        {
            if (!IsRecoverable(exception)) return false;
            return HasUiFrame(exception);
        }

        private static bool HasUiFrame(Exception exception)
        {
            if (new StackTrace(exception, false).GetFrames().Any(frame =>
                    frame.GetMethod()?.DeclaringType is { } type &&
                    type.Assembly == typeof(UiGuard).Assembly &&
                    type.FullName?.StartsWith(
                        "MahodAI.Civil3D.Plugin.CivilDelivery.UI.", StringComparison.Ordinal) == true))
                return true;
            if (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(HasUiFrame))
                return true;
            return exception.InnerException != null && HasUiFrame(exception.InnerException);
        }

        /// <summary>Tests replace the message box with a collector; production never sets this.</summary>
        internal static Action<string, Exception>? ReportOverride { get; set; }

        /// <summary>File-only record of a handled failure: type, message and stack.</summary>
        public static void Record(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
                File.AppendAllText(ErrorLogPath,
                    $"{DateTime.UtcNow:O} [{context}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // The log is best effort; the message box is the contract.
            }
        }

        public static void Report(string context, Exception ex)
        {
            var overrideSink = ReportOverride;
            if (overrideSink != null)
            {
                overrideSink(context, ex);
                return;
            }

            Record(context, ex);

            try
            {
                System.Windows.MessageBox.Show(
                    $"אירעה שגיאה בפעולה '{context}'.\n\n{ex.Message}\n\n" +
                    "אין אישור שהפעולה הושלמה או שהשינויים בוטלו. יש לבדוק את המצב לפני המשך העבודה.\n" +
                    $"פרטי השגיאה נרשמים ב:\n{ErrorLogPath}",
                    "Mahod Civil Delivery — שגיאה בממשק", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.OK,
                    System.Windows.MessageBoxOptions.RtlReading | System.Windows.MessageBoxOptions.RightAlign);
            }
            catch
            {
                // Nothing else is safe to do from a failing dispatcher callback.
            }
        }
    }
}
