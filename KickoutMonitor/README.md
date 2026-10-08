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


## Weekly Kickout workbook

In **Overkill Monitor > Kickout**, select dates and machine legend checkboxes, then
click **주간 보고 업데이트**. Only 1-1(-/+), 1-2(-/+) are checked by default;
2-1 and 2-2 can be enabled explicitly. The report includes only checked machines.
**보고 파일 선택** persists the selected existing .xlsx path.

The editable native-chart sheet **●Overkill 추이** follows the supplied report:
period totals, daily graphs, positive-only measure counts and the latest eight
Sunday–Saturday weeks. Partial weeks are marked; missing reports remain gaps.
ALL rows supply totals without adding overlapping measure counts.

The existing **●Overkill 리포트** receives inspected/NG/real/overkill counts and
the top three positive measures with counts per selected machine. Existing photos
and the manual reason/action column remain unchanged. Unselected machine rows
are hidden. Representative-case management has been removed; historical local
case files remain untouched.

Repeated overkill attempts count once per machine/polarity + Lot ID + Cell ID;
each measure also counts that cell once. Known lots span midnight. Missing lots
never cause cell-only merges. Historical counts are recalculated only when detail
identities adequately support them; aggregate-only history requires regenerating
its daily Summary. All attempt images remain available for comparison.

Updating also copies overkill images beside the workbook into
Overkill/MMdd-MMdd/line/measure/identity/inspection. Only selected dates and machines
are exported. A period marker distinguishes identical ranges in different years.
Images come from exact local review folders or their summary copies. Missing/empty
images are reported in export-status.txt and the diagnostic log; updating again
retries. Existing images are not deleted, including earlier exports for other
selections.

Close Excel before updating. The app validates the prepared workbook, verifies
the source has not changed, copies and verifies an adjacent .bak, then installs
the prepared workbook. A failed replacement restores a missing original from the
backup and retains the prepared file for recovery. Existing unrelated sheets,
hidden state, drawings and images are preserved. Validation uses a copy of the
user workbook; production originals are never changed during testing.

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


### IRS fetch completion and recovery

Review crops waits for every queued first-stage copy to succeed or report failure.
Review input and reload are blocked during the transition; pending unreviewed rows
do not require classification. Retry fetch retries saved failed/incomplete choices
without reloading the workbook, and refreshes the crop queue when already there.
Failures remain in the activity log; unverified saved images never enter crop review.

First-stage choices are persisted before network copying. Commits serialize their
read/update/write operation and replace the JSON via a temporary file, preventing
concurrent copies from overwriting other decisions. Reload restores choices
independently of image availability and marks incomplete copies for retry.
Reclassification removes obsolete owned outputs only after a successful copy.
CSV file parsing is shared across adjacent-day searches within one queue load;
copying reuses the exact inspection resolved by that load. Reload resets indexes.
