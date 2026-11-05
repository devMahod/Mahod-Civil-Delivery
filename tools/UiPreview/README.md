# סביבת בדיקה לממשק של Mahod Civil Delivery

כל מסכי הכלי (הפאנל ב-Civil 3D וחלונות ההחלטה) מעוצבים לפי קו העיצוב של מהוד: רקע אפור בהיר, חלונות לבנים מעוגלים,
צבע דיו אחד, פונט Rubik שמגיע עם התוסף (תיקיית Fonts ליד הקובץ), אייקוני קו בצבע אחד, וירוק רק במקום שיש לו משמעות
(השלב שבו נמצאים, סטטוס תקין). הערכה היא קובץ אחד: `MahodAI.Civil3D.Plugin/CivilDelivery/UI/CivilDeliveryTheme.xaml`.

## 1. לראות את המסכים בלי Civil 3D (שניות)

```powershell
cd tools\UiPreview
dotnet run -c Release
```

נפתח חלון גלריה: מימין רשימת כל המסכים, באמצע המסך שנבחר בגודל האמיתי שלו, עם נתוני דוגמה.
הגלריה טוענת את קובצי ה-XAML האמיתיים של הכלי ישירות מהמקור, כך שכל שינוי בעיצוב נראה מיד
(לחיצה חוזרת על המסך ברשימה טוענת אותו מחדש). הכפתור "פתח כחלון אמיתי" פותח את המסך כחלון רגיל.

תמונה של כל מסך (ל-PR או לסקירה):

```powershell
cd tools\UiPreview
dotnet run -c Release -- --snapshot out
```

## 2. לבדוק בתוך Civil 3D 2026

הדרך הקצרה: פקודה אחת שבונה את התוסף, מעתיקה אותו לתיקיית בדיקה מקומית
(`%USERPROFILE%\MahodCivilDelivery-test\plugin`), טוענת אותו ל-Civil 3D הפתוח (או פותחת אחד), פותחת שרטוט ואת הפאנל:

```powershell
.\tools\Test-InCivil3D.ps1 -Drawing "P:\data\6426\Civil3d\DD\CIVIL-6426-designid-mikta4.dwg"
```

שום דבר לא מותקן: בהפעלה הבאה של Civil 3D התוסף לא נטען. אחרי שינוי בקוד סוגרים את Civil 3D ומריצים שוב
(תוסף שכבר נטען אי אפשר להחליף בלי לסגור). בלי `-Drawing` הפאנל נפתח על השרטוט הפעיל; `-SkipBuild` משתמש בבנייה האחרונה.

ידנית:

```powershell
dotnet build MahodCivilDelivery\Mahod.CivilDelivery\Mahod.CivilDelivery.csproj -c Release -p:AutoCADVersion=2026
```

ב-Civil 3D: `NETLOAD` ← הקובץ `MahodCivilDelivery\Mahod.CivilDelivery\bin\Release\Mahod.CivilDelivery.dll`,
ואז הפקודה `MCD_CIVIL_DELIVERY` (או הכפתור Civil Delivery בלשונית Mahod). הפאנל, חלונות ההחלטה
וחלון ההתקדמות כולם בעיצוב החדש.

הערה למחשב הזה: ב-`global.json` של המאגר מבוקשת ערכת .NET 10, ומותקנת רק 8. הפקודות למעלה
עובדות כשמריצים אותן מתוך `tools\UiPreview` (יש שם `global.json` משלו) או מכל תיקייה מחוץ למאגר,
למשל: `cd $env:TEMP; dotnet build "<נתיב מלא ל-csproj>" ...`.

## 3. הבדיקות האוטומטיות של הפאנל

```powershell
cd $env:TEMP
dotnet test "<נתיב למאגר>\MahodAI.Civil3D.Plugin.Tests\MahodAI.Civil3D.Plugin.Tests.csproj" -p:AutoCADVersion=2026 --filter "Category!=RequiresCivil3D"
```

בדיקות הפריסה מריצות את ה-XAML האמיתי בלי Civil 3D ובודקות גבהים, ניגודיות, גלילה וכפתורים שלא נחתכים.
