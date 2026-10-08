# חבילת Civil Delivery למשתמש

גרסה 1.4.2, עדכון הפצה 8 באוקטובר 2026. מקור הקוד: `0febf19d449e2a6ce3f6f4df22abea0781203ff5`.

החבילה בנויה במתכונת CULVERT: ZIP המכיל בדיוק מתקין EXE ומדריך PDF בעברית. מחירון נתיבי ישראל האחוד מיולי 2026 מוטמע בתוסף. המתקין מכיל את אותו מדריך PDF שמופץ בנפרד.

[הורדת החבילה וקבצים נפרדים](https://github.com/devMahod/Mahod-Civil-Delivery/releases/tag/v1.4.2-r20261008)

[מדריך למשתמש, 10 עמודים](MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf)

## בניית החבילה

לאחר בניית מדריך ובדיקתו, הפעילו משורש המאגר:

```powershell
$guide = (Resolve-Path 'release/1.4.2/update-2026-10-08/MAHOD_CIVIL_DELIVERY_GUIDE_HE_1.4.2.pdf').Path
$guideSha = (Get-FileHash -LiteralPath $guide -Algorithm SHA256).Hash
./MahodCivilDelivery/installer/build-setup.ps1 -GuidePdf $guide -ExpectedGuideSha256 $guideSha -ExpectedVersion '1.4.2'
python ./MahodCivilDelivery/installer/package-distribution.py --guide $guide
```

התוצרים נמצאים ב־`MahodCivilDelivery/out/distribution`: ZIP, מתקין, מדריך ו־`SHA256SUMS.txt`. סקריפט האריזה בודק שהמדריך תואם למדריך שבמניפסט המתקין, שה־ZIP מכיל שני קבצים בלבד ושהם זהים למקור.

לפרסום ציבורי בונים ללא `MAHOD_CAD_KEY` וללא קובץ מפתח שנוצר מבנייה קודמת. מפתחות ומתקינים אינם נכנסים לקומיט.

## בדיקות

- בנייה לשני המארחים, אימות קובצי התוסף ו־Core, אימות המטען המוטמע במתקין וסריקת Defender נקייה.
- 57 בדיקות ממוקדות עברו עבור הקיבוץ, זהויות השיוך וראיות הסריקה.
- המדריך נרנדר ונבדק חזותית; פרק 12 מתאר את השיפורים האחרונים.
- אין בבדיקות אלה אישור חדש לכל תרחישי העבודה בתוך Civil 3D.

התיעוד והחבילה מ־7 באוקטובר ב־`../README.md` נשמרים כרשומה היסטורית. Hashes שם מתייחסים לחבילה ההיסטורית; לחבילה זו משתמשים ב־`SHA256SUMS.txt` שב־Release.
