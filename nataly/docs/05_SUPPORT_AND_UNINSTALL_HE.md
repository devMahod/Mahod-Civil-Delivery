# תמיכה והסרה

## אם משהו לא עובד

הכלי כותב יומן שלבים מפורט. **זה הדבר הכי שימושי שאפשר לשלוח.**

### איפה הכול נמצא

| מה | נתיב |
|---|---|
| יומני שלבים | `%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\logs\` |
| דוחות ריצה | `%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\runs\` |
| פרופיל הפרויקט | `%LOCALAPPDATA%\MahodAI_Civil3D\civil-delivery\profiles\6422\` |
| קובצי Excel | `Documents\MahodCivilDelivery\6422\` |

הדבק את הנתיב בסייר הקבצים (כולל הסימנים `%`).

### אם Civil נתקע

פתחי את היומן האחרון ב-`logs\`.
חפשי את השורה האחרונה שמתחילה ב-`BEGIN` **שאין אחריה** `END` — זו הפעולה שנתקעה.

```
[   4821 ms] BEGIN apply.sectionview_create | MCDV-1086 @ 218430.5,662104.2
```

השורה הזו שווה יותר מכל תיאור מילולי.

### מה לשלוח

1. את תיקיית `civil-delivery` כולה (ZIP)
2. צילום מסך של מה שראית
3. שורה אחת: מה עשית ומה קרה

---

## הסרה

**לוח הבקרה → תוכניות → Mahod Civil Delivery → הסר**

או ידנית:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\MahodAI_Civil3D\civil-delivery\Uninstall-MahodCivilDelivery.ps1"
```

ההסרה **משאירה** את הפרופילים, האומדנים ויומני הריצה — הם התוצרים שלך.
למחיקה מלאה הוסף `-PurgeData`.

סגרי את Civil 3D לפני ההסרה.

---

## עדכון גרסה

פשוט הריצי את המתקין החדש. הוא מגבה את הגרסה הקודמת עם חותמת זמן
ולא דורס את הגדרות הפרויקט שכבר אישרת.
