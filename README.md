# VisionMaster

VisionMaster is a Windows WPF tool for welding vision review workflows in production battery inspection.

Current executable:

`KickoutMonitor/publish/VisionMaster.exe`

Current modules:

- `KickoutMonitor`: KICKOUT NG/overkill review and IRS Review image gathering.
- `IRS Review`: loads IRS workbooks, displays raw images, lets the user classify/fetch Mavin crops, and saves results under `E:\KWAK\VisionMaster`.

Production files are treated as read-only. The app copies snapshots/images locally before saving review outputs.
