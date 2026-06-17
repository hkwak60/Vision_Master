# Vision Issue Tracker

Local Windows desktop issue tracker for mass-production vision inspection instruments.

## Current Prototype

- Local SQLite database stored in `data/vision_issues.db`
- Shared-PC workflow with worker selection, no login
- Lines: `1-1`, `1-2`, `2-1`, `2-2`
- Instruments per line: `Pinhole`, `Pouch Align`, `Lead`, `Sealing`, `Lead Align`, `Welding(+)`, `Welding(-)`
- Categories and subcategories:
  - Hardware: Camera, Lighting
  - Software: Program Crash
  - Recipe: Overkill, Underkill
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
- Selected-issue detail panel with quick status actions
- Scrollable selected-issue detail panel and issue tables
- Search quick filters for common report views

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
