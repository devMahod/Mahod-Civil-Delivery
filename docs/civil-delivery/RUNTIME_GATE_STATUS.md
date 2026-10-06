# Runtime gate status - candidate91

**Package revision:** 1.2.91
**MahodAI platform version:** 1.3.81.0
**Current candidate GUI status:** `PENDING`

PREPARED_NOT_TESTED. Candidate91 has no native acceptance. No installation or employee distribution is authorized by these scripts. New build/test receipts live outside this source tree under candidate91-r2.

Latest recorded host evidence: LIMITED_NATIVE89: Civil 3D 2027, 24.09.2026. Candidate89 was installed; PLAN sections-plan-20260924-165428-179c8a4d, VERIFY sections-verify-20260924-165713-7e922d5b (78/78) on STA-42676 output created by APPLY81 sections-apply-selected-20260923-113129-9b106b4f. Quantity scan89 was recorded (estimate-extract-20260924-170520-0aebd0fe), not a completed approved estimate. No fresh APPLY89, all-section/general-drawing/2026 acceptance or release approval is established. Previous pre-native statements that89 was not installed are stale.

Historical text below is reference context only, not a candidate91 result. Its pre-native statements that89 was not installed or never checked are explicitly superseded by the limited native89 record above.

---
## Historical candidate90 context (verbatim; NOT current status)

# Runtime gate status - candidate90

**Package revision:** 1.2.90
**MahodAI platform version:** 1.3.80.0
**Current candidate GUI status:** `PENDING`

PREPARED_NOT_TESTED. Candidate90 has no native acceptance. No installation or employee distribution is authorized by these scripts. New build/test receipts live outside this source tree under candidate90.

Latest recorded host evidence: LIMITED_NATIVE89: Civil 3D 2027, 24.09.2026. Candidate89 was installed; PLAN sections-plan-20260924-165428-179c8a4d, VERIFY sections-verify-20260924-165713-7e922d5b (78/78) on STA-42676 output created by APPLY81 sections-apply-selected-20260923-113129-9b106b4f. Quantity scan89 was recorded (estimate-extract-20260924-170520-0aebd0fe), not a completed approved estimate. No fresh APPLY89, all-section/general-drawing/2026 acceptance or release approval is established. Previous pre-native statements that89 was not installed are stale.

Historical text below is reference context only, not a candidate90 result. Its pre-native statements that89 was not installed or never checked are explicitly superseded by the limited native89 record above.

---
## Historical candidate89 context (verbatim; NOT current status)

# Runtime gate status — Mahod Civil Delivery

**Package revision:** 1.2.89
**MahodAI platform version:** 1.3.79.0
**Current candidate GUI status:** `PENDING`

**OFFLINE_VERIFIED_NATIVE_PENDING.** Core **2,126/2,126**, Plugin **3,027/3,027**, SaveGuidance **31/31** בכל אחד מהיעדים 2027 ו־2026; **10,368** הרצות בסך הכול, ללא כשל או דילוג במסנן האופליין. שבעה שערי מתקין עברו; זהות 182 PASS והסירוב הצפוי היחיד ל־ZIP עובדים. המתקין נמצא בהורדות לבדיקה חיה מורשית, לא להפצת עובדים. ההוכחה החיה האחרונה נשארת 81.

[ראיות שש המסילות](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate89/tests89.json) · [תוצאות וזהות](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate89/RESULTS89_HE.md).

## שינויי 89 והבדיקות שבוצעו באופליין

89 = מקור 88 הקפוא + תיקון שנמצא בסימולציה מלאה לפני התקנה: ה־fit מודע־ההסתרה מבוטא ביחס המבט שסיביל מדווח לתצוגה (ViewZoomPlan.WithViewAspect) לפני ההחלה — אחרת בדיקת הקריאה־חזרה של הניווט (1e-9) הייתה נכשלת בפער יחס של 0.14% והמסגור היה מדווח ככישלון למרות שהתצוגה זזה. 87 ו־88 נבנו, אומתו באופליין ולא הותקנו. כל שאר תיקוני 85–88 ללא שינוי.

- תיקון חסם התמחור שנצפה חי ב־84: אזור קורידור שנקרא במלואו אך ריק או חסר קצה (2000-DES: 0 הרכבות מיושמות, הוכח בקריאה בלבד דרך COM) מסווג כעת כממצא כיסוי של אותו קורידור בלבד, `EST-CORRIDOR-MATERIAL-COVERAGE-INCOMPLETE`, ולא כשגיאת QTO גלובלית. שורות עצמאיות משויכות ומתומחרות נכללות בטיוטה חלקית; אומדן מלא נשאר חסום; תחנה מחוץ לטווח, כפילות או ערך לא סופי נשארים שגיאה קשיחה. (patches 02, 07, 01, 10, 12 — קוד Codex, אומת על ידי Claude.)
- מסגור החתך מול הפאנל הצף: "הצג ואמת" ממקם את החתך בחלק הגלוי של חלון התצוגה הפעיל כשהפאנל צף מעליו. ב־85 התיקון לא נכנס לפעולה (שני חלונות תצוגה: SCREENSIZE 1475×788 ≠ חלון השרטוט 1912×788); 86 הוסיף איתור של חלון השרטוט המלא לפי שמות מחלקות חלון (AfxFrameOrView/OpenGL) — **ובבדיקה החיה של 86 (24.09) גם זה לא נכנס לפעולה**, כי ב־Civil 3D 2027 אין חלונות כאלה (חלון הציור הוא ACADDM_CHILD_DXGI_FLIP_MODE_VIEW_CLASS בתוך חלון המסמך). **תיקון 87:** חלון השרטוט נלקח מחלון המסמך עצמו (Document.Window, 1912×788 חי), עם מדיניות בחירה טהורה ונבדקת (`ViewZoomPlan.ChooseCanvasCandidate`: התאמה מדויקת ל־SCREENSIZE, אחרת החלון הקטן ביותר שמכיל את חלון התצוגה — לא משטח דפדפן מוטמע 1896×989); מלבן חלון התצוגה הפעיל נגזר לפי הפינות היחסיות של ViewportTableRecord ומוצלב מול SCREENSIZE (3%) ויחס המבט (2%); פאנל מעוגן או כל ספק מחזירים את המסגור הרגיל. ראיה: `cap7_8dbecf` (רום מוסתר ב־84), `evidence/live86/`. **88:** כל מועמד לחלון השרטוט (חלון המסמך, ואחריו ילדי החלון הראשי לפי מדיניות גודל) נבדק מול SCREENSIZE (3%) ויחס המבט (2%) והראשון שמתאים נבחר; חלון הפאנל נמדד דרך Win32 (חלון השורש של ה־ElementHost) עם Location/Size כגיבוי; וכל "הצג ואמת" כותב שורת `מסגור: …` (הענף שנבחר, חלון השרטוט, הפאנל, האזור הפנוי, ה־fit והתצוגה הקודמת) ביומן הפעילות וב־`stage_ui_verify_selected.log`, כדי ש"לא זז" יהיה ניתן להבחנה מ"לא נכנס לפעולה". +1 בדיקה עם הגאומטריה החיה המדויקת. (patches 03–06, 13 של 85; 4 קבצים ב־86; ב־87 אותם 4 קבצים, +11 בדיקות; ב־88 6 קבצים; **89:** ה־fit מבוטא ביחס המבט של התצוגה לפני ההחלה, +1 בדיקה; 3 קבצים.)
- טיוטת המדידות: שורות המדידה בגיליון הראשי הן שורה קומפקטית אחת (21pt) בסגנון ללא גלישה; גיליונות הממצאים/ההצעות/המקורות/פרטי הטקסט ללא שינוי. על סריקת 74,891 הרשומות המוקלטת: גובה שורה מרבי 21pt. (patches 09, 11.)
- דוח שטחי החתך: שורות הזהות 2–8 ממוזגות A:R כך שה־SHA-256 של השרטוט, הפרופיל והסריקה קריאים ב־Excel. (patch 08.)
- אימות האופליין עבר בשש מסילות מלאות: Core, Plugin ו־SaveGuidance בכל אחד מהיעדים 2027 ו־2026; המסנן היחיד היה `Category!=RequiresCivil3D`. מחזור השמירה נבדק עם קוד המוצר המקושר למעטפת דטרמיניסטית, לא עם Civil אמיתי.

ההרצה העתיקה את הקבצים הקפואים ותוספי ההקשר הדרושים לעותק מבודד; Core/Plugin הורצו מול DLLs זהים למטען המתקין. דוח כל מסילה כולל TRX/hash, משפחות ומספר בדיקות. SaveGuidance מתועד בנפרד כמבחן מחזור שמירה עם מעטפת אירועים ושני קובצי מוצר מקושרים ומזוהים, לא כהרצה חיה.

## ראיה חיה קודמת — אינה קבלת84

הראיה החיה האחרונה היא **1.2.81 / 1.3.71.0**, Civil 3D 2027.1, בתאריך 23.09.2026. בחתך STA-42676 נוצר View `BD9470`; אימות עבר 78/78, ושוב 78/78 לאחר שמירה ופתיחה מחדש. תצוגתו נבדקה בהגדלה. זו קבלה של חתך הבדיקה בלבד, לא של כל 28 החתכים או של כל השרטוט.

הסריקה החיה קראה 81,343 עצמים ו־79,180 מדידות בכ־24.697 שניות; הסטטוס הכולל נשאר Failed: 44 HATCH ו־27 עצמים באורך אפס לא נמדדו, ארבעה קורידורים לא מעודכנים, שלושה פערי כיסוי והיקף עבודות עפר לא מוכרע. רק שלושה מקרי HATCH אובחנו עצמאית. בטיוטת TEST-ONLY בלבד נכללו 292 רשומות בסכום; 78,888 נותרו מחוץ לו. הסכום 2,695,745.81 מעוגל לקבוצות BOQ; סכימת סכומים מעוגלים לכל עצם נותנת 2,695,745.75. ב־Excel נבדקו תאים ונוסחה מייצגים, לא חישוב מחדש של כל החוברת. פרופיל 6422 המקורי נשמר.

[מחזור חיי החתך](C:/Users/arthurf/Downloads/Cutz/Work/review-230926/native81/SECTION81_LIFECYCLE_HE.md) · [הבדיקות החיות](C:/Users/arthurf/Downloads/Cutz/Work/review-230926/native81/NATIVE81_PROGRESS_HE.md).

## פער האופליין הקודם

[tests83.json](C:/Users/arthurf/Downloads/Cutz/Work/review-230926/completion83-final/tests83.json) מוכיח Core1,429 ו־Plugin1,970 בכל הוסט, במסננים המסוימים בלבד. הבדיקות המרכזיות לחתכים, אומדן, יחידות וניווט כן נכללו. המסננים השמיטו 592 בדיקות Core ו־1,049 בדיקות Plugin לעומת הריצה המלאה של קלוד, וכן לא הריצו את פרויקט SaveGuidance בן31 המקרים. ב־84 לא משתמשים עוד במסנני שמות אלה.

## קבלה נדרשת

[מסלול הבדיקה החיה](../../runtime-gate/ARTHUR_10_MIN_GUI_GATE_HE.md): DLL/מקור נכונים; יחידות והסרת הצהרה; ניווט; חתך נבחר→אימות→שמירה/פתיחה→אימות; מדידה→שיוך→תמחור→יצוא וחישוב נוסחאות. מצב Failed או טיוטה חלקית יישארו גלויים; לא יומצאו כמויות/מחירים כדי לעבור. קבלה ב־2026 וב־2027 נרשמת בנפרד ורק על סמך הרצה ממשית.

## היסטוריה

88 (24.09.2026): נבנה ואומת באופליין (Core 2,125, Plugin 3,027, SaveGuidance 31 בכל מארח; מתקין `a78ffa78373f6891…`) אך **לא הותקן** — סימולציה מלאה של המסלול עם הגאומטריה החיה (`C:/Users/arthurf/Downloads/MahodAI-Plugin/handoff/claude-review-240926/evidence/sim88/`) הראתה שה־fit מודע־ההסתרה, שחושב ביחס הפיקסלים של החלון (1477/788), היה נכשל בבדיקת הקריאה־חזרה של התצוגה (1e-9) מול היחס שסיביל מדווח (1475/788, פער 0.14%) ומדווח ככישלון ניווט למרות שהתצוגה זזה; תוקן ב־89. [תוצאות 88](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate88/tests88.json) · [דוח 88](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate88/RESULTS88_HE.md). [גיבוי המסמך לפני 89](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate89/metadata-before89/docs/civil-delivery/RUNTIME_GATE_STATUS.md).

87 (24.09.2026): נבנה ואומת באופליין (Core 2,124, Plugin 3,027, SaveGuidance 31 בכל מארח; מתקין `ef15753ffb952e5e…`) אך **לא הותקן** — הוחלף ב־88 לפני הבדיקה החיה, כי איתור חלון השרטוט שלו נשען על שתי הנחות מארח (`Document.Window`, `PaletteSet.Location/Size`) שאי אפשר לאמת מחוץ ל־Civil; 88 מסיר את התלות בהן ומדווח על עצמו. [תוצאות 87](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate87/tests87.json) · [דוח 87](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate87/RESULTS87_HE.md). [גיבוי המסמך לפני 88](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate88/metadata-before88/docs/civil-delivery/RUNTIME_GATE_STATUS.md).

סבב חי של 86 (24.09.2026, Claude דרך Windows-MCP על העותק המקומי): הותקן על ידי ארתור (Program Files, AppVersion 1.2.86, DLL `ca7609fe…` = המתקין `2242c7c90a9c496e…`); PLAN 1.2.86 (28 חתכים, STA-42676 Ready); "הצג ואמת" על STA-42676 — אומת, אך **המסגור מול הפאנל הצף לא נכנס לפעולה**: איתור חלון השרטוט לפי שמות מחלקות (AfxFrameOrView/OpenGL) לא מצא דבר ב־Civil 3D 2027 (הוכח בקריאה בלבד: חלון המסמך 1912×788 ב־(4,207), חלון הציור ACADDM_CHILD_DXGI_FLIP_MODE_VIEW_CLASS, SCREENSIZE 1475×788, פאנל צף 591–1311) — תוקן ב־87 וממתין להוכחה חיה. [ראיות הסבב](C:/Users/arthurf/Downloads/MahodAI-Plugin/handoff/claude-review-240926/evidence/live86/) · [תוצאות 86](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate86/tests86.json) · [דוח 86](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate86/RESULTS86_HE.md). [גיבוי המסמך לפני 87](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate87/metadata-before87/docs/civil-delivery/RUNTIME_GATE_STATUS.md).

סבב חי של 85 (24.09.2026, חלקי, Claude דרך Windows-MCP על העותק המקומי): המודול הטעון 1.2.85 אומת; חתך STA-42676 הקיים אומת 78/78; סריקה מודרכת בפרופיל 6422 (81,343 ישויות, 79,180 רשומות, 25 שניות) עם ממצאי קורידור `EST-CORRIDOR-OUT-OF-DATE` ×4 ו־`EST-CORRIDOR-MATERIAL-COVERAGE-INCOMPLETE` ×3 (2000-DES: 0 הרכבות) **וללא** `EST-CORRIDOR-MATERIAL-QTO-FAILED` — חסם 84 תוקן; טיוטת מדידות: 79,180 שורות בגובה 21pt; דוח שטחי חתך: שורות 2–8 ממוזגות A:R (569 מדידות); תמחור בפרופיל TEST-ONLY (שיוך U51.06.1900 מאושר בפרופיל): 297 שורות HW-CURB מתומחרות, 292 נכללו בסכום 2,695,745.81 ₪ (5 מתומחרות נשארו מחוץ לסכום — הסיבה לא נבדקה), 78,888 שורות לא נכללו; טיוטה חלקית בלבד, אומדן מלא חסום; "ייצא טיוטה מתומחרת" יצר חוברת בחמישה גיליונות (כתב כמויות, לא מתומחר, ממצאים, עקבה, זהות ראיות) ו־Excel חישב SUM = 2,695,745.81. נמצא: תיקון המסגור של 85 לא נכנס לפעולה כי השרטוט עובד עם שני חלונות תצוגה (SCREENSIZE 1475×788 מול חלון שרטוט ברוחב 1909) — תוקן ב־86 וממתין להוכחה חיה. יצירה חדשה של חתך לא בוצעה (דורשת אישור ארתור). [ראיות הסבב](C:/Users/arthurf/Downloads/MahodAI-Plugin/handoff/claude-review-240926/evidence/live85/) · [תוצאות 85](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate85/tests85.json) · [דוח 85](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate85/RESULTS85_HE.md). [גיבוי המסמך לפני 86](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate86/metadata-before86/docs/civil-delivery/RUNTIME_GATE_STATUS.md).

סבב חי של 84 (24.09.2026, לא הושלם): חתך STA-42676 הקיים אומת 78/78 גם אחרי שמירה/פתיחה מחדש; סריקה 25 שניות; טיוטת מדידות ושטחי חתך יוצאו ונפתחו ב־Excel; מחיר פרויקט נשמר בפרופיל TEST-ONLY; **התמחור נחסם** (0 שורות נכללות) בגלל סיווג אזור הקורידור הריק — תוקן ב־85; כיתוב רום הוסתר מאחורי הפאנל — תוקן ב־85. [פערי הקבלה של 84](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/native84/ACCEPTANCE_GAPS84_HE.md) · [יומן הסבב](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/native84/NATIVE84_ACCEPTANCE.md) · [ביקורת Claude וחבילת התיקונים](C:/Users/arthurf/Downloads/MahodAI-Plugin/handoff/claude-review-240926/NATIVE84_REVIEW_AND_1.2.85_PATCHES.md) · [קבלת קריאת הקורידורים](C:/Users/arthurf/Downloads/MahodAI-Plugin/handoff/claude-review-240926/evidence/corridor-regions-com-probe-20260924.json). [תוצאות 84](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate84/tests84.json) · [דוח 84](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate84/RESULTS84_HE.md) · [גיבוי המסמך לפני 85](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate85/metadata-before85/docs/civil-delivery/RUNTIME_GATE_STATUS.md).

[דוח83](C:/Users/arthurf/Downloads/Cutz/Work/review-230926/completion83-final/RESULTS83_HE.md) · [דוח82](C:/Users/arthurf/Downloads/Cutz/Work/review-230926/completion82-final/tests82.json) · [גיבוי המסמך לפני84](C:/Users/arthurf/Downloads/Cutz/Work/review-240926/candidate84/metadata-before84/docs/civil-delivery/RUNTIME_GATE_STATUS.md). אין שינוי של תוצאות היסטוריות או קבלת עובדים במסמך זה.
