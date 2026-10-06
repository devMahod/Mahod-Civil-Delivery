using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    public static class CorridorMaterialAvailabilityPolicy
    {
        public const string UnavailableCode = "EST-CORRIDOR-MATERIAL-MODEL-NOT-AVAILABLE";

        public static DeliveryFinding? Evaluate(
            int corridorCount, bool hasReadFailure, string projectProfileId,
            string drawingPath, string drawingHash)
        {
            // A failed collection is not evidence that the drawing has no corridor.
            if (corridorCount != 0 || hasReadFailure) return null;
            return new DeliveryFinding
            {
                Code = UnavailableCode,
                Domain = "estimate",
                Severity = FindingSeverity.Info,
                ProjectProfileId = projectProfileId,
                Title = "לא נמצא קורידור בשרטוט הפעיל — נפחי חומרי מבנה לא חושבו",
                Message = "אפשר להמשיך לבדוק אורכים, שטחים ובלוקים שנמדדו מגיאומטריית השרטוט והפניותיו. " +
                    "שרטוט גיאומטריה או סימון, כגון GM/SM, אינו מעיד שקיים בו מודל קורידור. " +
                    "פרמטרי עובי H/HL בתת־הרכבה אינם כשלעצמם שטח או נפח לפרויקט; היעדר מדידת חומר אינו כמות אפס.",
                RecommendedAction = "למדידת נפחי חומר יש להשתמש במודל Civil עם קורידור מעודכן, צורות חומר וקודי חומר " +
                    "בחתכי התחנות. לקביעת שטח שכבת חומר נדרשת הגדרת מדידה מפורשת; אין להסיק אותו מעובי בלבד.",
                SourceRefs =
                {
                    new ProvenanceRef
                    {
                        SourceKind = "civil-model", SourcePathOrUri = drawingPath,
                        DrawingChecksum = drawingHash,
                    },
                },
            };
        }
    }
}
