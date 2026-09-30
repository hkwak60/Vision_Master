# KickoutMonitor

A focused, read-only Welding Vision KICKOUT review application.

The application reads a user-selected production date, queues `JUDGE = NG`
records excluding OCR and AGING cells, displays only the NG side's raw/overlay
images one at a time, and
copies the complete original cell folder after the user classifies it:

```text
E:\KWAK\VisionMaster\<line>(<polarity>)\NG\<defect>\<original-folder>
E:\KWAK\VisionMaster\<line>(<polarity>)\OVERKILL\<defect>\<original-folder>
```

Production CSVs and images are never modified, moved, renamed, or opened with
exclusive access.

## Connection diagnostics

Each queue load checks SMB connectivity and reports D/E/F/G access in the
on-screen log. Named shares such as `\\IP\E` are preferred because that is the
production-PC share format; administrative shares such as `\\IP\E$` are tested
only as a fallback. The successful form is reused for CSV and image access.

## Working cache

Unclassified review images are copied sequentially from the exact CSV image-path
columns into:

```text
E:\KWAK\VisionMaster\.temp\<line>(<polarity>)\<date>\<original-folder>
```

The UI decodes reduced previews from this local disk cache rather than retaining
the queue in memory or repeatedly reading the Welding PC. Real NG and Overkill
cache folders are removed after the complete original cell folder is safely
copied to its final classification.

Use Left/Right to move between images in the current cell. Use Up/Down to move
between cells. Scroll or use the touchpad to pan the image. Hold Ctrl while
scrolling to zoom at the pointer location; Shift + scroll pans horizontally.
The initial zoom is 60%, and click-dragging pans the zoomed image. Pending queue
rows are white, Real NG and Multi-Defect NG rows are green, and Overkill rows
are red.
Multi-Defect NG is used when one cell has multiple confirmed defects that are
not represented by a single result-CSV defect value. It is treated as confirmed
NG and stored under:

```text
E:\KWAK\VisionMaster\<line>(<polarity>)\NG\MULTI_DEFECT\<original-folder>
```
Moving between images in the same cell preserves the zoom level and normalized
viewport center, so the same defect location remains in view. Moving to another
cell resets the image to the default 60% view.
The log records when every NG candidate for the loaded production date has been
classified as Real NG or Overkill. It also records when queue fetching starts
and when the queue is ready for review, and automatically follows the newest
entry. Multiple Welding lines/polarities and an inclusive production date range
can be loaded into one queue; every item retains its source machine so review
copies still go to the correct output folder.

Daily CSV discovery accepts the base result file and numbered continuations:
`..._YYYYMMDD.csv`, `..._YYYYMMDD_1.csv`, `..._YYYYMMDD_2.csv`, and so on.
Files with suffixes such as `- Copy`, `_Copy`, or `_defect` are ignored.


## Weekly Kickout workbook and representative cases

In **Overkill Monitor > Kickout**, select the report dates and click **주간 보고 업데이트**.
**보고 파일 선택** saves a fixed existing .xlsx path in WeeklyReportPath (initially
C:\\KWAK\\4. ESHG\\CS_Weekly_Report.xlsx). The app adds or refreshes only its managed
●Kickout 과검 sheet. The user's ●Overkill 리포트, hidden sheets, images and other
workbook content are preserved. Close Excel before updating; failures retain the
original and are written to the existing Overkill Monitor diagnostic log.
A successful replacement retains one adjacent workbook.bak.

The sheet includes all eight lines/polarities, period counts, daily graphs,
positive-only defect lists, and eight Sunday–Saturday weeks ending in the selected
end date's week. A partial final week is marked *. Missing reports remain gaps,
zero remains zero, and covered-day counts explain incomplete weeks. Totals use
ALL rows, not summed defects. Real NG counts only reviewed real defects; remaining
NG is shown separately as pending. Native Excel charts remain editable.

Open a Kickout detail record, select an actual overkill row, and choose
**대표 사례 추가**. Select an image from that inspection's folder and enter the reason
and action. **대표 사례 관리** restores, reorders or removes up to three cases per
period and line. Save retries a missing image. Decisions, descriptions and copied
images are stored below the existing NG_Summary/.weekly root; no production files
are modified. A locally saved image remains usable offline. Automatic sheet edits
are replaced next time; edit representative-case content in the app.

## IRS shared training batches

IRS **Review crops** opens the crop-classification stage using completed source
reviews, even when other loaded source rows remain pending. After final classes
are saved, **Generate / Add to current batch** adds the classified items from the
loaded queue. Pending, No Need, unknown folders and NEED_TO_SIMULATE/raw items do
not enter training. IRS summary reports remain available; they no longer produce
duplicate classified crop datasets; existing legacy exported images are preserved on regeneration. Rulebase and simulation-support files remain
part of the existing summary workflow.

IRS and DLNG pool by product + crop + polarity for classification, or product +
crop for segmentation. Older/newer inspection dates use the latest matching active
batch. Same inspection/crop inputs deduplicate across origins; manifests retain
IRS/DLNG provenance. An active IRS-only sample can be reclassified and re-added.
Conflicting classes from separate sources never silently overwrite each other:
confirm the intended label, explicitly exclude the conflicting active sample in
Training collection, then re-add the reviewed sample.

Missing/empty/incomplete files retain failed status and do not count as collected.
Use **Retry pending copies** in Training collection, or the IRS add button again.
Review decisions persist independently. Generate Dataset exports to
DLNG_REPORT/DATASET, directly into class folders, without classification ActiveMap
images. Export freezes the batch while retaining counts and allowing repeat exports;
a later explicit add starts a new batch when needed. No historical IRS reviews are
automatically collected and no IRS collection records are inserted into DLNG review
statistics. Existing review/collection stores are backed up before modification.
