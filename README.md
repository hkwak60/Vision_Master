# Vision Issue Tracker

Local Windows desktop issue tracker for mass-production vision inspection instruments.

## User Manual

See [USER_MANUAL.md](USER_MANUAL.md) for the full Korean user manual with feature descriptions, examples, deployment notes, and DB backup guidance.

## Current Prototype

- Local SQLite database stored in `data/vision_issues.db`
- Shared-PC workflow with worker selection, no login
- Lines: `1-1`, `1-2`, `2-1`, `2-2`
- Instruments per line: `Pinhole`, `Pouch Align`, `Lead`, `Sealing`, `Lead Align`, `Welding(+)`, `Welding(-)`
- Categories and subcategories:
  - Hardware: Camera, Lighting
  - Software: Program Crash, UI, PLC, Other
  - Recipe: Overkill, Underkill, Add Measure, Bypass/Unbypass
  - Camera Grab Fail
  - Production
  - Other
- Active statuses shown in Open Issues:
  - Action Required
  - Monitoring
- Current worker profile selector:
  - Hojun Kwak
  - Kijung Kim
  - Jihoon Yun
  - Jisub Yun
- New issue entry
- Open issue lookup
- Search and Excel export
- Dashboard summary cards for active issue counts
- Visual line/instrument selector with dedicated line buttons and vision-type buttons
- Selected line and vision buttons remain highlighted
- Issue entry and search filters support multiple selected vision instruments
- Selected-issue detail panel with quick status actions
- Scrollable selected-issue detail panel and issue tables
- Search quick filters for common report views
- Search and Excel rows are sorted by issue time with sequential report numbers
- Header language selector for English and Korean UI labels
- Search date range defaults to the first and latest issue times

## Run

Use the bundled Python runtime from Codex, or any local Python 3 installation with `openpyxl` available:

```powershell
& "C:\Users\hkwak\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" app.py
```

## Build To EXE Later

The planned packaging tool is PyInstaller:

```powershell
pyinstaller --onefile --windowed --name VisionIssueTracker app.py
```

That step requires PyInstaller to be installed in the Python environment used for packaging.
