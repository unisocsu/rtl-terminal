# RTL Terminal

טרמינל Windows עצמאי (WPF, .NET 8) עם:

- **תמיכה ב-RTL** — כל שורת פלט נבדקת ומקבלת `FlowDirection` (ימין-לשמאל / שמאל-לימין) לפי התו המכוון הראשון שבה (עברית/ערבית מול לטינית).
- **כרטיסיות (Tabs)** — כל כרטיסייה מריצה תהליך shell עצמאי משלה.
- **ConPTY אמיתי** — במקום Process עם stdin/stdout מופנים (Redirect), האפליקציה משתמשת ב-Windows Pseudo Console API (`CreatePseudoConsole`) כדי לקבל התנהגות טרמינל אינטראקטיבי אמיתית.
- **נתיב ה-shell נשלף מהרישום** — `Services/ShellPathResolver.cs` לא מריץ `"cmd.exe"` בתור מחרוזת קבועה; הוא קורא את הנתיב האמיתי מ-`HKCU`/`HKLM` תחת `Software\Microsoft\Windows\CurrentVersion\App Paths\cmd.exe`, עם נפילה חזרה ל-`System32` אם המפתח חסר.

## מבנה

```
RtlTerminal/
├── RtlTerminal.csproj
├── app.manifest
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / .cs          # ניהול כרטיסיות
├── Services/
│   ├── ShellPathResolver.cs       # שליפת נתיב ה-shell מהרישום
│   ├── ConPtySession.cs           # עטיפת ConPTY (P/Invoke)
│   └── RtlTextHelper.cs           # זיהוי כיווניות טקסט לכל שורה
├── Terminal/
│   ├── TerminalBuffer.cs          # מסך תווים אמיתי: grid, סמן, גלילה, צבעים (SGR)
│   └── VtParser.cs                # state machine שמפרש ANSI/VT ומפעיל שינויים על ה-buffer
├── Models/
│   └── TerminalTab.cs             # מחזיק Session + Buffer לכל כרטיסייה
└── Controls/
    ├── TerminalCanvas.cs          # ציור ישיר של ה-grid (FrameworkElement.OnRender) + סמן מהבהב
    └── TerminalView.xaml/.cs      # מארח את ה-Canvas, מזין לו פלט, מעביר מקלדת ל-stdin
```

### איך זה עובד עכשיו (v2 - "מסוף אמיתי")

בגרסה הקודמת כל פלט פשוט הצטרף לסוף טקסט (append-only), ולכן כל דבר שדרש "לצייר מחדש שורה" (מחיקת תו, redraw של CLI כמו Claude Code) לא עבד נכון. עכשיו:

1. `ConPtySession` מייצר בייטים גולמיים מה-shell.
2. `TerminalView` מפענח אותם ל-UTF-8 (עם `Decoder` שמחזיק מצב בין קריאות, כדי לא לשבור תווים רב-בייטיים שנחתכים בין chunks).
3. `VtParser` "קורא" את התווים תו-תו, ומפרש: תזוזות סמן (`CUU/CUD/CUF/CUB`), מיקום סמן (`CUP`), מחיקת שורה/מסך (`EL`/`ED`), צבעים ועיצוב (`SGR`), הצג/הסתר סמן (`DECTCEM`), אזור גלילה (`DECSTBM`).
4. כל פעולה כזו **משנה בפועל** את ה-`TerminalBuffer` — grid של `Cell` (שורה × עמודה), כמו שכל טרמינל אמיתי עובד.
5. `TerminalCanvas` מצייר את ה-grid ישירות עם `DrawingContext`, כולל זיהוי RTL לכל שורה (שורה שזוהתה כעברית/ערבית מוצגת "מראה" ומיושרת לימין).

## מהירות פתיחה

הבנייה עברה מ-`PublishSingleFile` (exe בודד שמכיל את כל ה-runtime דחוס בפנים, ומתפרק/מתחלץ מחדש לכל הרצה) לפרסום כתיקייה רגילה (`self-contained`, בלי single-file) עם קימפול `ReadyToRun` (מקדים חלק ניכר מעבודת ה-JIT לזמן build). זה אמור לקצר משמעותית את זמן הפתיחה, וגם להפחית סיכוי לסריקת Defender/SmartScreen איטית שקבצי exe בודדים גדולים ולא-חתומים נוטים לעורר.

**חשוב:** מעכשיו ה-artifact הוא **תיקייה** ולא קובץ exe בודד — יש להריץ את `RtlTerminal.exe` מתוך התיקייה כולה (כל הקבצים ב-`publish/` נדרשים ביחד, לא רק ה-exe).

## בנייה

הבנייה מתבצעת ב-GitHub Actions (`.github/workflows/build.yml`) על `windows-latest`.

הפלט: תיקיית פרסום self-contained עם ReadyToRun (ראו "מהירות פתיחה" למעלה) שמועלית כ-artifact בשם `RtlTerminal-win-x64`.

## דיבאג - איך לעזור לי לתקן באגים תצוגה במקום שאנחש

יש מצב לוג שמתעד את כל הבייטים הגולמיים שמגיעים מה-shell (ומה שאנחנו שולחים אליו), כדי שבבאג הבא נוכל להסתכל על מה שבאמת קרה במקום לנחש.

**להפעלה:** לפני הרצת ה-exe, הגדר משתנה סביבה:

```powershell
$env:RTLTERMINAL_DEBUG = "1"
.\RtlTerminal.exe
```

הלוג נכתב ל-`%TEMP%\RtlTerminal-debug.log` (בד"כ `C:\Users\<שם משתמש>\AppData\Local\Temp\RtlTerminal-debug.log`), ומכיל לכל chunk גם hex וגם טקסט עם קודי בקרה מוצגים כ-`\xNN` (כדי לראות רצפי CSI/OSC בבירור), ולכל הקשה שנשלחת - את הבייטים שנשלחו.

אם תתקל שוב בבאג (למשל ה-backspace שמוחק שורה שלמה, או היפוך טקסט), תפעיל את זה, תשחזר את הבאג, ותשלח לי את הקטע הרלוונטי מהלוג - זה יאפשר תיקון מדויק במקום ניחוש.



## גלילה להיסטוריה (Scrollback)

- **גלגלת עכבר** - גלילה מעל הטרמינל חושפת שורות שגללו מעל המסך (עד 5000 שורות אחרונות, ב-`TerminalBuffer`).
- **פס גלילה** - צד ימין של כל כרטיסייה, לניווט ישיר.
- **הקלדה כלשהי מחזירה אוטומטית לתחתית** (live) - כמו ברוב הטרמינלים, כדי שלא תישאר "תקוע" בהיסטוריה בלי לראות מה שאתה מקליד.
- אם גללת אחורה וממשיך פלט חדש להגיע, התצוגה נשארת יציבה על אותו תוכן היסטורי (לא "בורחת" קדימה) - עד שתקליד או תגלול בחזרה בעצמך.
- הסמן המהבהב מוצג רק בתצוגה החיה (offset=0); בהיסטוריה אין סמן.

## מגבלות ידועות (v3)

- **Alternate screen buffer בסיסי בלבד** — נתמך `?47`/`?1047`/`?1049` (מסך נפרד + שחזור cursor ל-1049), בלי scrollback להיסטוריה של המסך המשני - זה כמעט תמיד לא מורגש כי אפליקציות alt-screen ממילא מציירות הכל מחדש כשהן עולות.
- **256/true-color לא נתמכים עדיין** — רק 16 הצבעים הבסיסיים של ANSI (`ApplySgr` ב-`TerminalBuffer`). קודי `38;5;n`/`38;2;r;g;b` יתעלמו כרגע.
- **זיהוי RTL הוא היוריסטי** ברמת runs (לא Unicode Bidi Algorithm מלא עם embedding levels ו-isolates) — מספיק לרוב הפלט (עברית עם מילים/נתיבים באנגלית), אבל לא למקרי קצה מורכבים.
- לא נבדק מקומית (הסביבה שיצרה את הקוד היא לינוקס ללא .NET SDK) — יתכנו שגיאות build קטנות.
