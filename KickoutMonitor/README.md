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
