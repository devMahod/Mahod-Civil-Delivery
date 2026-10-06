using System;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using MessageBoxOptions = System.Windows.MessageBoxOptions;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    public partial class CivilDeliveryControl
    {
        /// <summary>
        /// "מדריך למשתמש" (standalone tool only; the button stays collapsed in the MahodAI build). Opens a private
        /// temporary copy of the guide PDF that belongs to the RUNNING bundle (resolver 182B0B5E + temp copy 331C872B,
        /// Codex 15:31), so a PDF reader never holds the installed file open during an upgrade. No other guide is ever
        /// chosen and the bundle file itself is never opened; a failure says exactly what is missing.
        /// </summary>
        private void OnOpenUserGuide(object sender, RoutedEventArgs e)
        {
#if MAHOD_CD_STANDALONE
            const string Title = "מדריך למשתמש";
            const MessageBoxOptions Rtl = MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign;
            var assemblyLocation = typeof(CivilDeliveryControl).Assembly.Location;
            var guide = MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryGuidePath.Resolve(assemblyLocation);
            if (guide.Status != MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryGuidePath.State.Ready)
            {
                var detail = guide.GuidePath ?? assemblyLocation;
                Log("פתיחת מדריך: " + guide.ReasonCode + "; " + detail);
                SetStatus("המדריך המקומי אינו זמין — יש לתקן את התקנת Mahod Civil Delivery");
                MessageBox.Show(
                    "לא ניתן לאתר את המדריך של ההתקנה הפעילה.\n" +
                    "יש לתקן את התקנת Mahod Civil Delivery. לא נבחר מדריך מהתקנה אחרת.\n\n" +
                    "נתיב: ‎" + detail + "‎\nקוד: " + guide.ReasonCode,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Rtl);
                return;
            }

            var copy = MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryGuideTempCopy.Prepare(guide);
            if (copy.Status != MahodAI.Civil3D.Plugin.Runtime.CivilDeliveryGuideTempCopy.State.Ready)
            {
                Log("הכנת עותק לקריאה של המדריך נכשלה: " + copy.ReasonCode + "; " + guide.GuidePath);
                SetStatus("המדריך לא נפתח — לא ניתן היה להכין עותק לקריאה");
                MessageBox.Show(
                    "המדריך קיים בהתקנה, אך לא ניתן היה להכין ממנו עותק לקריאה.\n" +
                    "קובץ ההתקנה לא נפתח ישירות. יש לנסות שוב, ואם הבעיה חוזרת — לתקן את ההתקנה.\n\n" +
                    "קוד: " + copy.ReasonCode,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Rtl);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(copy.TempPath!) { UseShellExecute = true });
                Log($"מדריך נפתח מעותק לקריאה: {copy.TempPath} (SHA-256 של המדריך המותקן {copy.SourceSha256})");
                SetStatus("נשלחה בקשה לפתיחת המדריך בקורא ה-PDF");
            }
            catch (Exception error)
            {
                Log("פתיחת המדריך נכשלה: " + error.Message);
                SetStatus("המדריך קיים, אך לא נפתח — יש לבדוק קורא PDF ושיוך קבצים ב-Windows");
                MessageBox.Show(
                    "המדריך קיים, אך Windows לא הצליח לפתוח אותו.\n" +
                    "יש לבדוק שקורא PDF מותקן ושקובצי PDF משויכים אליו.\n\n" +
                    "נתיב: ‎" + copy.TempPath + "‎",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, Rtl);
            }
#endif
        }
    }
}
