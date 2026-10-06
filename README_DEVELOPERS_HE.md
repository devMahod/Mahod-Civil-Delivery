# Mahod Civil Delivery: חבילת קוד מקור למפתחים (בנייה b41, 06.10.2026)

תוסף ל-Civil 3D 2026/2027: כתב כמויות (מדידה, שיוך למחירון, טיוטת אומדן) וחתכי כביש (תכנון, יצירה, אימות).
**מצב:** גרסת בדיקה. זו לא גרסת שחרור. מספר הגרסה בקוד ובקובץ ההתקנה הוא 1.4.0, וזיהוי הבנייה הוא b41.

## זהות הבנייה שבחבילה
- **‏MANIFEST** (payload של ההתקנה): `AFA81736359393E80EBC091C02A1099695F436B9E065DA4FF5B02A8E952F0B83`
- **‏Setup**: ‏`Mahod_Civil_Delivery_Setup_1.4.0.exe`, ‏SHA-256 `DCE8C84911B78BEC10E369CE52214DAE654E0312F70A9B410ACCD6F87FA3549B`
- **‏DLL ל-2027**:
  - plugin `C87D2E0A5AB2272FF94F270057AD4659446E3981C5790991E62DB332A648BEF8`
  - core `67776E8C582B9F43F82B5D1A18C66D88F8A4A20C2E39C13384F520B8A833DE53`
- **‏DLL ל-2026**:
  - plugin `01C0C57155892814E53B90AA90CFD5197B5887C23A5860F93D9A0E48F8CD23A9`
  - core `AA83687714059087FABA97648F9E7CBD234DFBE6D1871146955C353223B92D21`
- **הקוד בחבילה:** זהה לעץ שממנו נבנתה הבנייה. ‏SHA-256 לכל קובץ ב-`SHA256SUMS.txt`.

## מבנה
- `MahodAI.Civil3D.Plugin/`: התוסף ל-Civil (UI ב-WPF, קריאת DWG, חתכים, אומדן). ‏`CivilDelivery/` הוא לב המוצר.
- `MahodAI.CivilDelivery.Core/`: לוגיקה טהורה בלי Autodesk. מתקמפל גם לתוך המוצר העצמאי.
- `MahodCivilDelivery/`: המוצר העצמאי (Mahod.CivilDelivery*), ה-installer ב-`installer/` ו-`VERSION`.
- `MahodAI.Core.Tests/`, ‏`MahodAI.Civil3D.Plugin.Tests/`, ‏`MahodCivilDelivery/Mahod.CivilDelivery*.Tests/`: בדיקות.
- `MahodAI.bundle/`: קלט לבדיקות (מבנה bundle).
- `round-1.4.1/`: מסמכי הסבב האחרון:
  - `PLAN_1.4.1_HE.md`: תוכנית וטבלת קבלה.
  - `STATUS_1.4.1_HE.md`: סטטוס לארתור.
  - `chain_b18.sh` + `gate.sh`: שרשרת הבנייה המלאה.

## בנייה ובדיקות (Windows, ‏.NET SDK לפי global.json)
```
dotnet test MahodAI.Core.Tests/MahodAI.Core.Tests.csproj -c Release -p:AutoCADVersion=2027 --filter "Category!=RequiresCivil3D"
dotnet test MahodAI.Civil3D.Plugin.Tests/MahodAI.Civil3D.Plugin.Tests.csproj -c Release -p:AutoCADVersion=2027 -p:DeployPlugin=false --filter "Category!=RequiresCivil3D"
cd MahodCivilDelivery
dotnet test Mahod.CivilDelivery.Core.Tests/Mahod.CivilDelivery.Core.Tests.csproj -c Release --filter "Category!=RequiresCivil3D"
dotnet test Mahod.CivilDelivery.Tests/Mahod.CivilDelivery.Tests.csproj -c Release --filter "Category!=RequiresCivil3D"
dotnet build Mahod.CivilDelivery/Mahod.CivilDelivery.csproj -c Release -p:AutoCADVersion=2026 -p:MahodCivilDeliveryGuideEnabled=true
powershell -File installer/build-setup.ps1 -GuidePdf <guide.pdf> -ExpectedGuideSha256 <sha> -ExpectedVersion 1.4.0
powershell -File installer/Test-Installer.ps1
```
- **המדריך ל-build-setup.ps1:** ה-PDF לא נכלל בחבילה הזו. הוא נמצא בחבילת המהנדסים: `MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.0.pdf`, ‏SHA-256 `8816E9629A6DAEB01AB71FE114D5BEB24E0AF5B3D5B73F8F8EDF5EB91D83F1D4`.
- **ההוכחה שהחבילה עצמאית:** החבילה חולצה לתיקייה ריקה, ומשם רצו ארבע ריצות הבדיקה ובניית 2026. התוצאות זהות לבנייה המקורית.
- **תוצאות בנייה b41:**
  - CORE 3578/0 (דולגו 0).
  - PLUGIN 3646/0 (דולגו 4).
  - STDCORE 3291/0 (דולגו 0).
  - STD 2578/0 (דולגו 4).
  - Test-Installer 20/0.
- **הפניות Autodesk:**
  - ל-2027 מ-Civil 3D 2027 המותקן.
  - ל-2026 מחבילות compile-only.
- **באחריות הבונה:** ההתקנה נבנית בלי SolidCompression, כדי ש-Defender לא יחשוד בה. סריקת Defender היא חלק מ-build-setup.ps1.

## כללי עבודה שנלמדו (חשוב)
- כל שינוי בהתנהגות נבדק בתוך Civil 3D על **עותק** של פרויקט. בדיקות אוטומטיות לבד אינן הוכחה.
- לעולם לא לכתוב לשרתי הפרויקטים (F:, P:). עובדים על עותק מקומי.
- **EOL:** בקבצי המקור יש CRLF ו-LF מעורבים. לשמור על ה-EOL של כל שורה; כש-git autocrlf פעיל, git apply משנה אותו.
- אסור לנחש יחידות, רוחבים או שמות רצועות. החלטה הנדסית נשארת אצל מהנדס, ונרשמת בפרופיל עם שם המאשר.

## פתוח למפתחים
1. **עדכון אוטומטי:** ‏MahodUpdater הקיים בפלטפורמה תומך במוצר אחד (MahodAI.bundle, ‏feed אחד, מפתח חתימה אצל בעל הפלטפורמה). ההמלצה היא להרחיב אותו לריבוי מוצרים, ולא לבנות מנגנון נפרד.
2. **גיליון "הצעות שיוך" בייצוא:** כדאי עמודות קריאות (תיאור, סיבה, ציון) במקום JSON גולמי.
3. **בדיקת פאנל צר בתוך Civil:** טרם נעשתה. יש מבחן WPF (‏SectionPaletteLayoutTests).
4. **Civil 3D 2026:** הרכיב נבנה ונבדק אוטומטית בלבד, ולא הופעל בתוך 2026.
5. **יצירת חתכים בפרויקט 984:** חסומה עד החלטות מהנדס: זוג משטחים, כללי סימוני תכנית לשכבות HW-CURB, ‏0-ASFALT, ‏0-TR-KIR, ושמות רצועות.
