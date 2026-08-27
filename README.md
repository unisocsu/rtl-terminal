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
│   ├── AnsiSequenceStripper.cs    # ניקוי בסיסי של קודי ANSI/VT
│   └── RtlTextHelper.cs           # זיהוי כיווניות טקסט לכל שורה
├── Models/
│   └── TerminalTab.cs
└── Controls/
    └── TerminalView.xaml/.cs      # תצוגת פלט + העברת מקלדת ל-stdin
```

## בנייה

הבנייה מתבצעת ב-GitHub Actions (`.github/workflows/build.yml`) על `windows-latest`, כי הפרויקט משתמש ב-Windows API-ים (P/Invoke ל-`kernel32.dll`) שלא רלוונטיים/זמינים בסביבת לינוקס.

הפלט: exe עצמאי (`self-contained`, `PublishSingleFile`) שמועלה כ-artifact בשם `RtlTerminal-win-x64`.

להרצה מקומית (על מחשב Windows עם .NET 8 SDK):

```powershell
dotnet build RtlTerminal/RtlTerminal.csproj -c Release
```

## מגבלות ידועות (v1)

- **רינדור ANSI בסיסי בלבד** — כרגע `AnsiSequenceStripper` רק **מסיר** קודי צבע/עמדת-סמן במקום לפרש אותם. פלט צבעוני (כמו `git status` או `ls` עם צבעים) יוצג כטקסט רגיל ללא צבע. שדרוג עתידי: state machine מלא ל-VT100/xterm.
- **זיהוי RTL הוא היוריסטי**, לא אלגוריתם Unicode Bidi מלא — מספיק לרוב פלטי המסוף (נתיבים בעברית, טקסט מעורב שורה-שורה), אבל לא לטקסט דו-כיווני בתוך אותה שורה עם מבנה מורכב.
- אין עדיין תמיכה ב-PowerShell/פרופילים בתוך ה-UI (אבל `ShellPathResolver.ResolvePowerShell()` / `TryResolvePwsh()` כבר קיימים לשימוש עתידי).
- זהו סקאפולד ראשוני שלא קומפל באופן מקומי (הסביבה שיצרה את הקוד היא לינוקס ללא .NET SDK/NuGet) — יכול להיות שיהיו שגיאות build קטנות בסבב הראשון של ה-Actions. אם כן — תעתיקי/תעתיק את הלוג ונתקן.

## רעיונות להמשך

- פירוש CSI/SGR אמיתי (צבעים, bold) במקום strip.
- תמיכה בבחירת shell מתוך תפריט (cmd / PowerShell / pwsh) לכל כרטיסייה חדשה.
- שמירת היסטוריית פקודות / scrollback buffer עם חיפוש.
