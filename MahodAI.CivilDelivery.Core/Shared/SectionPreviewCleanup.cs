using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>Owns cleanup ordering without depending on a Civil graphics runtime.</summary>
    public static class SectionPreviewCleanup
    {
        public static void RequireActiveOwner(long? owner, long? activeOwner, bool ownerIsOpen)
        {
            if (owner == null || owner == 0 || !ownerIsOpen)
                throw new InvalidOperationException(
                    "לא ניתן לזהות שרטוט פתוח שבבעלותו התצוגה. ידיות התצוגה נשמרו לניסיון ניקוי חוזר; יש לפתוח את פרטי התקלה בתמיכה.");
            if (owner != activeOwner)
                throw new InvalidOperationException(
                    "התצוגה שייכת לשרטוט אחר. חזור לשרטוט שבו נוצרה התצוגה ולחץ 'נקה תצוגה', ואז המשך לשרטוט הרצוי.");
        }

        public static void Clear<THandle, TContext>(
            ICollection<THandle> handles,
            Func<TContext> enterOwnerContext,
            Action<TContext, THandle> erase,
            Action<THandle> dispose,
            Action<Exception>? disposalWarning = null)
            where TContext : IDisposable
        {
            // No host/context access is permitted when there is nothing to erase.
            // Document activation/destruction can happen without a graphics manager.
            if (handles.Count == 0) return;

            using var context = enterOwnerContext();
            var remaining = new List<THandle>();
            var failures = new List<Exception>();
            foreach (var handle in handles.ToArray())
            {
                try { erase(context, handle); }
                catch (Exception ex)
                {
                    remaining.Add(handle);
                    failures.Add(new InvalidOperationException(
                        "Failed to erase a section-preview drawable.", ex));
                    continue;
                }

                try { dispose(handle); }
                catch (Exception ex) { disposalWarning?.Invoke(ex); }
            }
            handles.Clear();
            foreach (var handle in remaining) handles.Add(handle);
            if (failures.Count > 0)
                throw new AggregateException(
                    "Section preview could not be cleared completely; failed handles were retained for retry.",
                    failures);
        }
    }
}
