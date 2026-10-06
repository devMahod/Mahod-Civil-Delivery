using System;
using System.Linq;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    public sealed partial class FamilyDecisionRow
    {
        /// <summary>FamilyRecognitionAssist records the request context of an assistant proposal as this inferred line.</summary>
        private const string AiContextPrefix = "ai_context=";

        /// <summary>
        /// Withdraws the assistant's answer that came from the image question with request context <paramref name="contextId"/>,
        /// after that image was replaced or removed. Only that answer goes: the CAD proposal, saved decisions, other groups and
        /// an answer of another request stay. (ApplyAssistant with no result is not a clear: it keeps <see cref="AiProposal"/>.)
        /// A family picked from the withdrawn suggestion stays picked but is not silently kept as an approvable manual choice:
        /// the group is unchecked and must be checked and attested again.
        /// </summary>
        internal bool WithdrawAssistantAnswer(string contextId)
        {
            if (string.IsNullOrEmpty(contextId)) return false;
            if (AiProposal != null && !AiProposal.Inferred.Contains(AiContextPrefix + contextId, StringComparer.Ordinal)) return false;
            var chosenFromAi = ChosenFromAi;
            AiProposal = null;
            AiStatus = "תשובת העוזר שהתבססה על תמונה קודמת הוסרה, כי התמונה הוחלפה או הוסרה. אפשר לשאול שוב." +
                       (chosenFromAi
                           ? " המשפחה שנבחרה לפי ההצעה נשארה ברשימה, אבל סימון האישור של הקבוצה בוטל: יש לבדוק ולסמן מחדש אם היא נכונה."
                           : string.Empty);
            Changed(nameof(ChosenFromAi));
            if (chosenFromAi) IsSelected = false;
            return true;
        }
    }
}
