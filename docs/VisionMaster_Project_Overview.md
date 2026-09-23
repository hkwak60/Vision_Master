# VisionMaster Project Overview

Rework identity, timestamp matching, grouping, saved-review compatibility and validation are specified in [Rework-safe inspection review](Rework_Review.md).

This document is a handoff guide for reviewers and maintainers who need to understand VisionMaster quickly without losing the important production rules. It complements `docs/VisionMaster_Rules.md` by describing the application structure, shared workflows, queue behavior, image lookup conventions, decision persistence, and report outputs across all current review modules.

## 1. Product Purpose

VisionMaster is a WPF desktop tool for reviewing welding vision production data. It reads production CSVs, IRS Excel lists, raw images, and Mavin crop images from production PCs, presents queue items to an operator, records human decisions locally, and generates summary workbooks plus copied image datasets for training, reinspection, or production reporting.

The application is built around these principles:

- Production sources are read-only. Never modify, delete, rename, move, or overwrite CSVs, raw images, crop images, or folders on production PCs.
- All persistent review decisions, copied images, intermediate staging, and generated summaries belong under the local VisionMaster storage root.
- Missing or inaccessible source files are expected in real production. The app should log those problems, keep the UI usable, and allow the operator to continue reviewing other items.
- Large CSV scans and summary exports should show activity progress and should avoid freezing the WPF UI thread.

## 2. Repository And Runtime Layout

The solution lives under:

`C:\KWAK\6. Vision History\KickoutMonitor\KickoutMonitor.sln`

The published executable normally lives under:

`C:\KWAK\6. Vision History\KickoutMonitor\publish\VisionMaster.exe`

The source is split into four projects:

- `KickoutMonitor.Domain`: shared domain models, settings, machine definitions, rule settings, review records, queue item records, enums, and validation.
- `KickoutMonitor.Application`: feature-level queue and summary orchestration services. These services decide what to scan and how domain records are built, but avoid WPF-specific concerns.
- `KickoutMonitor.Infrastructure`: file-system, CSV, Excel, crop lookup, image copy, JSON persistence, report writing, and production path mapping.
- `KickoutMonitor.App`: WPF shell, views, view models, preview loading, commands, keyboard shortcuts, and user-facing activity logs.

The app starts in `KickoutMonitor.App/App.xaml.cs`. Startup creates a single-instance mutex named `Local\VisionMaster.SingleInstance`, loads settings through `JsonSettingsStore`, creates infrastructure services, wires the feature view models, and opens `MainWindow`.

The dashboard order is:

1. `KickoutMonitor`
2. `IRS Review`
3. `DLNG Review`
4. `NG/Bypass Monitor`
5. `Flagged`
6. `LOG Monitor`
7. `Settings`

## 3. Storage Root And Local Files

The default storage root is:

`E:\KWAK\VisionMaster`

If the configured drive/root is unavailable, `AppStorage` falls back to:

`%LocalAppData%\KickoutMonitor`

The app creates common local folders:

- `.staging`: local snapshots and stable copies of source files.
- `.temp`: temporary image preview/candidate folders.
- `NG_Summary`: Kickout Monitor summary output.
- `NG_Bypass_Summary`: NG/Bypass Monitor summary output.
- `DLNG_REPORT`: DLNG report and dataset output.
- `Flagged_Summary`: Flagged Review summary output.
- `<line-polarity>\NG`: Kickout local reviewed copies.
- `<line-polarity>\OVERKILL`: Kickout local overkill copies.
- `<line-polarity>\IRS_LEAK`: IRS local first-stage copied results.
- `<line-polarity>\DLNG`: DLNG local copied/review support.
- `<line-polarity>\NG_BYPASS_MONITOR`: NG/Bypass local classified copies.
- `<line-polarity>\FLAGGED_REVIEW`: Flagged Review local first-stage copied results.

Important JSON files:

- `settings.json`: machine, path, and rule configuration.
- `kickout-reviews.json`: Kickout Monitor review decisions.
- `ng-bypass-reviews.json`: NG/Bypass Monitor review decisions.
- `dlng-reviews.json`: DLNG crop-pair review decisions.
- `flagged-items.json`: shared flags saved from all image review panes.
- `flagged-reviews.json`: Flagged first-stage review decisions.
- `flagged-dataset-reviews.json`: Flagged final-stage dataset decisions.

## 4. Machine And Path Model

VisionMaster currently targets Welding plus/minus machines. Default machines are:

- `1-1(-)`: line `1-1`, anode, IP `10.112.99.181`, model `E81C`
- `1-1(+)`: line `1-1`, cathode, IP `10.112.99.182`, model `E81C`
- `1-2(-)`: line `1-2`, anode, IP `10.112.99.66`, model `E81C`
- `1-2(+)`: line `1-2`, cathode, IP `10.112.99.67`, model `E81C`
- `2-1(-)`: line `2-1`, anode, IP `10.112.99.71`, model `E81C`
- `2-1(+)`: line `2-1`, cathode, IP `10.112.99.72`, model `E81C`
- `2-2(-)`: line `2-2`, anode, IP `10.112.99.77`, model `E69B`
- `2-2(+)`: line `2-2`, cathode, IP `10.112.99.78`, model `E69B`

Production CSVs are located from the machine data drive, normally `D`, using configured result path segments:

`\\<ip>\<data-drive>\Files\Data\Result\Day\...`

Raw and crop images use image drives, normally `E`, `F`, and `G`, with image path segments:

`\\<ip>\<image-drive>\Files\Image\...`

Crop image lookup must follow the raw image drive whenever the production CSV provides a raw-image path. If a row points to raw images on `F:`, crop lookup should prefer the matching `F` share. Configured image drives are fallback only when the raw drive cannot be inferred.

Mavin crop roots are bounded to:

`\\<ip>\<drive>\Files\Image\<model>\<yyyy>\<MM>\<dd>\Mavin\<crop-folder>`

Do not broad-scan production PCs.

## 5. Shared Production Data Rules

Daily production CSV scanning uses:

- `DailyCsvLocator` to locate date-specific production CSVs.
- `ReadOnlySnapshotService` to copy live or current-day files before parsing when needed.
- CSV readers with shared read access so production writers are not blocked.
- `CsvSupport.UniqueHeaders` style duplicate-header handling.
- Date-range filtering at the row level, not only file-name level.

Cell IDs starting with `OCR` or `AGING` are globally ignored across review features. This rule applies to Kickout, DLNG, NG/Bypass, and any future production-CSV-based queue unless explicitly changed.

A production report day is not midnight-to-midnight. The reporting window starts at `06:00:00` on the selected date and ends before `06:00:00` the next day.

## 6. Shared Image Review Behavior

Most review modules use the same three-pane interaction:

- Left pane: queue grid.
- Center pane: image viewer, image index, navigation, zoom, and flag/unflag control.
- Right pane: review/classification buttons.

The queue is visually stateful:

- Existing review-status ordering applies to whole rework groups; attempts within each group stay chronological.
- Reviewed items are colored green where the module supports state coloring.
- Copy or lookup failures can be colored red.
- Rework duplicate rows in NG/Bypass are subtly yellow.

Queue navigation must operate on the displayed/sorted queue view, not the original unsorted source list. If the operator sorts by time, cell, defect, side, or other columns, Up/Down should walk that displayed order.

Image navigation should work even when focus is not inside the queue grid. If no queue row is selected after classification, navigation should continue from the current item.

The flag icon belongs at the same header level as the image index, not inside the zoomable image canvas. This keeps it clickable and prevents it from panning/zooming with the image.

## 7. Shared Shortcuts

Common shortcuts:

- Left/Right: previous/next image.
- Up/Down: previous/next displayed queue item.
- Number keys `1` through `9`: select classification final classes by their leading number where the current decision pane contains numbered classes.

Module-specific shortcuts:

- Kickout Monitor: `R` Real NG, `O` Overkill, `M` Multi-Defect NG, `I` Ignore.
- IRS first stage: `R` Rulebase, `U` Undetectable.
- IRS final stage: `R` Real for segmentation, `O` Overkill for segmentation, `N` No Need to Retrain.
- DLNG: `R` Real, `O` Overkill, `N` No Need to Train for segmentation/fallback decisions.
- NG/Bypass Monitor: `R` Real, `O` Overkill.
- Flagged Review: first stage follows IRS-style decisions; final stage follows IRS/DLNG final-class shortcuts.

## 8. Classification Vs Segmentation Crop Rules

Classification crop models store files under class subfolders. The source class is the subfolder name. Classification image pairs are typically:

- `SourceMap`
- `ActiveMap`

Segmentation crop models store crop files directly in the crop folder. Segmentation image pairs are typically:

- `SourceImg`
- `_mask` or same-stem `.png`

Classification review overlays should show:

`<source class> / <Upper|Lower>`

Segmentation review overlays should show:

`Segmentation / <Upper|Lower>`

Classification export is split by how the human final class compares to the source class:

- `미검_오검`: source class was OK but final class is NG, plus other non-overkill switches that require retraining review.
- `과검`: source class was NG but final class is OK.
- `정상검출`: source class matches the final class.

Segmentation decisions are:

- `Real`
- `Overkill`
- `No Need to Retrain` or `No Need to Train`, depending on module wording.

Current segmentation export behavior:

- `Real` exports to `Real`.
- `Overkill` exports to `Overkill`.
- `No Need` records the decision but should not be exported as a training dataset.

## 9. Kickout Monitor

Kickout Monitor reads Welding production CSVs for selected line/polarity machines and a selected date range.

Queue rules:

- Include `JUDGE = NG`.
- Ignore global OCR/AGING cell prefixes.
- Use `UPPER_JUDGE` and `LOWER_JUDGE` to decide which side is NG.
- Upper raw side uses image indexes `0_0`, `0_1`, `0_2`.
- Lower raw side uses image indexes `1_0`, `1_1`, `1_2`.
- If both sides are NG, both sides are relevant.

Review decisions:

- `Real NG`: counted as real NG and images are saved.
- `Overkill`: counted as overkill and images are saved under overkill folders.
- `Multi-Defect NG`: summarized as multi-NG.
- `Ignore`: marks reviewed, but is excluded from counts and image saving.

Summary rules:

- Summary uses the 06:00-to-06:00 production-day window.
- Summary blocks if any matching NG item in the report window is unreviewed.
- Overkill rate means `overkill / total inspected`.
- NG rates are line/polarity specific.
- Production images are copied only into local/report output, never modified in place.

## 10. IRS Review

IRS Review starts from an IRS Excel workbook, not directly from production CSV queue criteria.

Workbook compatibility:

- The original workbook format and newer production workbook formats should both be supported.
- The reader should be robust to column order and header wording changes where the same information exists.
- Required concepts include equipment, vision type, production date/time, cell ID, camera location, image/folder reference, request state, second judgment, and second judgment reason.

Queue load behavior:

- Browsing/opening an IRS Excel file automatically loads the IRS queue.
- The old manual `Load IRS Queue` button has been removed.
- Include rows where `Request for NG Cell OUT == Request`.
- Ignore first judgment fields for queue eligibility.
- Display the `2nd judgment / reason` separately from the zoomable image layer.

Machine mapping examples:

- `PACKAGE #1-1 + Welding Plus` maps to `1-1(+)`.
- `PACKAGE #1-1 + Welding Minus` maps to `1-1(-)`.

First-stage behavior:

- Raw images are displayed, not crop overlays.
- The initial displayed raw image is the second image when multiple raw images exist. The image order itself must not change, so Left moves to the first image and Right moves to the third image.
- The operator chooses IRS-style first-stage categories.

First-stage decisions:

- `Rulebase`
- `Undetectable`
- `Crop_A` left/right
- `Crop_B` left/right
- `Crop_micro` LL/LM/MM/MR/RR
- `Crop_micro_tabside` left/right
- `GAP`
- `SEPA`
- `SEPA_SHOULDER` left/right

Rulebase and Undetectable are exclusive. Crop categories can be multi-selected. Reclassifying a first-stage row replaces prior local saved results for that row, but only inside VisionMaster local storage.

IRS crop lookup:

- Uses the same bounded Mavin root rules as other crop features.
- Follows the raw image drive when possible.
- TOP maps to upper-side crop filenames.
- BTM or BOTTOM maps to lower-side crop filenames.
- GAP may appear as `GAP_DL` or `Gap_DL`.

Generate Dataset:

- Requires all first-stage IRS rows to be reviewed.
- If any first-stage row is missing a decision, the app logs the missing cells so the operator can return to them.
- The final-stage queue is based on saved first-stage results, not the original workbook.
- Normal crop items are grouped into crop pairs.
- `NEED_TO_SIMULATE` items display the three raw images from the defect side.

Final-stage class rules:

- `Crop_A(-)`: `01_OK_TOP_ANODE`, `02_OK_BACK_ANODE`, `03_NG_TORN`, `04_NG_PTCL`, `05_NG_FOLDED`, `No Need to Retrain`
- `Crop_A(+)`: `01_OK_TOP_CATHODE`, `02_OK_BACK_CATHODE`, `03_NG_TORN`, `04_NG_PTCL`, `05_NG_FOLDED`, `No Need to Retrain`
- `Crop_B(-)`: `01_OK_ANODE`, `02_NG_TORN`, `03_NG_PTCL`, `04_NG_FOLDED`, `No Need to Retrain`
- `Crop_B(+)`: `01_OK_CATHODE`, `02_NG_TORN`, `03_NG_PTCL`, `04_NG_FOLDED`, `No Need to Retrain`
- `Crop_micro`: `01_OK_TAB`, `02_OK_BTM`, `03_OK_QNG_DENT`, `04_NG_TORN_DENT`, `05_NG_TORN_CRACK`, `06_NG_TORN_VERTICAL_CRACK`, `No Need to Retrain`
- `Crop_micro_tabside`: `01_OK_TAB_SIDE`, `02_OK_NG_MARK`, `03_QNG_WRINKLE`, `04_NG_SIDE_TORN`, `05_NG_SIDE_PTCL`, `No Need to Retrain`
- Segmentation-style folders: `Real`, `Overkill`, `No Need to Retrain`

Additional class filtering:

- For `Crop_A`, camera location limits OK choices.
- TOP hides `02_OK_BACK_*`.
- BTM/BOTTOM hides `01_OK_TOP_*`.
- NG final classes remain visible for both camera locations.

IRS summary:

- Summary generation should log step-by-step progress.
- Heavy summary and copy work should run off the UI thread.
- Classification exports use `Dataset\Classification\미검_오검`, `Dataset\Classification\과검`, and `Dataset\Classification\정상검출`.
- Segmentation-style items export under `Dataset\Segmentation`.
- `No Need to Retrain` decisions are not exported for retraining.
- Rulebase raw folders are outside `Dataset`, under `Rulebase\<line-polarity>\<2nd-judgment-reason>\<original-folder-name>`.
- Rulebase export must be date-specific to the loaded IRS list and line/polarity specific.
- Saved image names should preserve original source filenames.

## 11. DLNG Review

DLNG Review reads production CSVs over a selected date range and queues crop-pair items for selected DLNG models.

Model selection:

- Model/crop selections are displayed all at once.
- Defaults should be unchecked.
- The `All` button checks all model selections.
- Queueing should only search selected models, not every DLNG mapping.

Eligible CSV rows:

- `JUDGE = DLNG`
- `JUDGE = C-NG`
- `JUDGE = QNG`
- `JUDGE = NG`

`Q-NG` is not eligible by default. Blank or unsupported `JUDGE-DEFECT` values are ignored. OCR/AGING cells are globally ignored.

Side selection:

- For `JUDGE = NG`, choose sides where `UPPER_JUDGE` or `LOWER_JUDGE` equals `NG`.
- For `DLNG`, `C-NG`, and `QNG`, choose sides where `UPPER_<JUDGE-DEFECT>-JUDGE` or `LOWER_<JUDGE-DEFECT>-JUDGE` equals `BYPASS_NG`.
- `SEPA_SHOULDER` production rows may use `SEPA_SHOULDER_DL`; both the defect value and side-judge alias should be supported.
- If only one side caused the defect, only that side's crop should be fetched. Do not fetch both upper/lower for GAP, SEPA, SEPA_SHOULDER, HORNMARK, LEADEDGE, or other DLNG models unless both sides actually match.

DLNG defect mapping:

- `A_L`, `A_R` -> `Crop_A`
- `B_L`, `B_R` -> `Crop_B`
- `BEAD_CNT` -> `SEGMENTATION`
- `SEPA_DL`, `SEPA`, `SEPA_TAB`, `SEPA_LEFT`, `SEPA_RIGHT` -> `SEPA`
- `SEPA_SHOULDER`, `SEPA_SHOULDER_DL` -> `SEPA_SHOULDER`
- `Micro_LL`, `Micro_LM`, `Micro_MM`, `Micro_MR`, `Micro_RR` -> `Crop_micro`
- `B_DIM_L`, `B_DIM_R` -> two review pairs, one from `HORNMARK` and one from `LEADEDGE`
- `H_DIM_L`, `H_DIM_R` -> `HORNMARK`
- `GAP`, `GAP_DL` -> `Gap_DL`
- `Tab_Burr_LL`, `Tab_Burr_LM`, `Tab_Burr_LB`, `Tab_Burr_RR`, `Tab_Burr_RB`, `Tab_Burr_RM`, `TABSIDE_L`, `TABSIDE_R` -> `Crop_micro_tabside`

Crop lookup:

- Classification folders recurse through class subfolders.
- Classification pair: `SourceMap` plus `ActiveMap`.
- Segmentation folders search direct files.
- Segmentation pair: `SourceImg` plus `_mask` or same-stem `.png`.
- HORNMARK and LEADEDGE filename matching must handle spacing variants such as `HORN LEFT`, `HORN MARK L`, and similar polarity/side tokens.
- `B_DIM_L/R` creates exactly two review pairs: HORNMARK and LEADEDGE.
- If mapped crops are missing, fallback displays the three raw production images from the review side and exports the raw folder under `NEED_TO_SIMULATE`.

DLNG final-class rules:

- Classification models reuse IRS final-class groups.
- `Crop_A` OK options are side-aware: upper hides back OK, lower hides top OK.
- `Crop_micro` OK options are side-aware: upper shows `01_OK_TAB`, lower shows `02_OK_BTM`.
- `Crop_micro_tabside` defaults to 40% zoom.
- Segmentation/fallback buttons are `Real`, `Overkill`, and `No Need to Train`.
- `No Need to Train` records a decision but is not exported for training.

DLNG report behavior:

- Summary/report generation may proceed with an incomplete visible queue, but it should only summarize queued and reviewed cells.
- It must not rescan and summarize all models when only selected/queued models were reviewed.
- Reports support date ranges and produce separate production-day outputs.
- File names include the model name to avoid overwriting same-date outputs.
- Generating another model for the same date must not delete or overwrite datasets from previously generated models.
- Reports are stored under `DLNG_REPORT\REPORT`.

DLNG training dataset export:

- `Generate Dataset` sits next to `Generate DLNG Report`.
- It is enabled after a summary has been generated.
- It accepts a date range.
- It collects reviewed crop images into one simpler training directory by model and date range:

`DLNG_REPORT\DATASET\<model-name>\<date-range>\<class-name>\...`

Classification models use their class names. Segmentation models use `Real` and `Overkill`. `No Need` decisions are skipped.

## 12. NG/Bypass Monitor

NG/Bypass Monitor is a measure-driven production CSV review module.

Header inputs:

- Selected line/polarity machines.
- Start date and end date for queue loading.
- Measure text input.
- `UPPER` checkbox.
- `LOWER` checkbox.
- `Bypassed` checkbox.
- `Skip NG` checkbox, enabled only when `Bypassed` is checked.

Column naming:

- Upper column: `UPPER_<Measure>-OK/NG`
- Lower column: `LOWER_<Measure>-OK/NG`

Input validation:

- Measure is required.
- At least one side checkbox is required.
- If a required measure column is missing from a CSV header, log a clear warning with machine/date/file/column.
- Missing headers should not silently look like zero NGs.

Queue rules:

- If `Bypassed` is unchecked, target value is `NG`.
- If `Bypassed` is checked, target value is `BYPASS_NG`.
- If `Skip NG` is checked, rows whose overall `JUDGE` is `NG` are filtered out of queueing and inspected-row summary counts.
- Upper and lower matches become separate side-specific queue items.
- Each queue item displays three raw images from the selected side.
- OCR/AGING cells are globally ignored.

Review decisions:

- `Real`
- `Overkill`

Rework duplicate rule:

- Production can rework a kicked-out NG cell by reinserting the same cell.
- If the same cell in the same Lot ID receives the same measure-side NG from the same line/polarity again, keep it in the queue for human review.
- Mark duplicate/rework queue rows with a slight yellow hint so the user knows they are rework repeats.
- Summary counts should count only the first occurrence per machine, Lot ID, cell and measure-side for initial matched count, Real, and Overkill.
- Image saving behavior does not change for duplicate/rework rows.

Summary behavior:

- Supports multi-date summary generation.
- Each production day gets its own date folder using the 06:00-to-06:00 rule.
- Within each date folder, create a measure-name subfolder.
- Summary root:

`NG_Bypass_Summary\NG_Bypass_Summary_<yyyyMMdd>\<Measure>\...`

Local classified copies are separate from Kickout:

`<storage-root>\<line-polarity>\NG_BYPASS_MONITOR\REAL\<measure>\...`

`<storage-root>\<line-polarity>\NG_BYPASS_MONITOR\OVERKILL\<measure>\...`

## 13. Flagged Review

The flagging feature lets operators mark the current cell/side for deeper follow-up while they are reviewing in another module.

Where flagging exists:

- Kickout Monitor
- IRS Review
- DLNG Review
- NG/Bypass Monitor

Flag behavior:

- The flag button records the current cell/side/context and does not interrupt the current review.
- If viewing raw/overlay images, flag the currently displayed side.
- If viewing crop images, flag the crop item's side/camera.
- If both sides exist in the source context, only the currently displayed side is flagged.
- Flags are upserted into `flagged-items.json`.

Flagged module:

- `Load Flagged Queue` loads unsummarized flags.
- `Previously Flagged` loads summarized flags.
- In unsummarized mode, the image header button acts as unflag.
- In previously flagged mode, the button acts as re-flag so the item can be reviewed again.
- The queue displays the flag date and follows DLNG-style reviewed coloring.
- Initial display is three raw non-overlay images from the flagged side.
- Shortcuts `R` and `N` are supported where they match the current review stage.

Flagged first-stage decisions:

- Reuses IRS first-stage selections.
- Also includes flagged-specific crop options: Hornmark L, Hornmark R, LeadEdge L, LeadEdge R, and Bead.
- Crop fetching must be side-appropriate. If the original defect was upper, do not fetch lower crop images.
- Hornmark/LeadEdge crop matching must respect L/R polarity tokens and filename spacing variants.

Flagged dataset and summary:

- `Generate Dataset` builds an IRS-style final subclass queue.
- Previous decisions should load when revisiting previously flagged items.
- `Generate Summary` exports to:

`Flagged_Summary\Flagged_Summary_<yyyyMMdd_HHmmss>\...`

- Classification exports use `Dataset\Classification\미검_오검|과검|정상검출`.
- Segmentation exports use `Dataset\Segmentation`.
- Rulebase raw folders are outside `Dataset` under:

`Rulebase\<line-polarity>\<2nd-judgment-reason>\<original-folder-name>`

- Flags are marked summarized only after summary generation succeeds.
- Summarized flags leave `Load Flagged Queue` and appear under `Previously Flagged`.

## 14. LOG Monitor

LOG Monitor appears on the dashboard as `Image NG retry analysis`. It is a separate module from the review queues above. A reviewer should inspect its current implementation before changing it, because it was added after the original Kickout/IRS flow and may not share all review-store behavior.

At minimum, preserve these expectations:

- Keep production sources read-only.
- Log missing/inaccessible files instead of crashing.
- Do not block the dashboard or other review modules.
- Keep any output separate from Kickout, IRS, DLNG, NG/Bypass, and Flagged report roots unless the user explicitly requests integration.

## 15. Settings

Settings are loaded through `JsonSettingsStore` and defaulted by `VisionMasterSettings.CreateDefault()`.

Settings include:

- Storage root.
- Production data/image path segments.
- Machine definitions.
- Kickout rules.
- IRS first-stage selections.
- IRS final-class groups.
- DLNG eligible judges.
- DLNG defect-to-crop mappings.
- DLNG segmentation classes.

If `settings.json` is missing, defaults are created. If it is invalid, the app should load defaults and surface a warning in Settings rather than failing silently.

When settings evolve, make sure existing user settings get missing defaults inserted. For example, existing saved segmentation class lists should gain `Overkill` without requiring the user to delete `settings.json`.

## 16. Output Naming And Copy Rules

General rules:

- Preserve original source filenames when copying crops or raw images to summary/dataset folders.
- Do not prepend machine, line, polarity, or generated metadata to filenames unless the user explicitly requests it.
- Put line/polarity in directory hierarchy, not in filename, when line separation is needed.
- Avoid deleting prior generated model/date datasets when generating a new model/date summary.

Classification model line/polarity hierarchy:

`<crop-folder>\<line-polarity>\<final-class>\...`

Examples:

`Crop_A\1-1(-)\03_NG_TORN\...`

`Crop_B\1-1(+)\01_OK_CATHODE\...`

Segmentation hierarchy:

`<crop-folder>\<Real|Overkill>\...`

Fallback raw hierarchy:

`NEED_TO_SIMULATE\<crop-folder>\<cell-id-or-cell-folder>\...`

## 17. Performance And UI Responsiveness

Large production CSV files can make the app appear frozen if scanning or summary export happens synchronously. Queue and summary features should:

- Snapshot live/current-day CSVs before parsing when appropriate.
- Stream CSV rows instead of loading more data than needed.
- Filter as early as possible by selected machines, date range, selected model/measure, eligible judge/value, and ignored cell prefixes.
- Search only selected crop folders.
- Follow the raw-image drive for crop lookup instead of trying every drive first.
- Cache crop folder enumerations per machine/date/model/folder/drive during a scan.
- Log progress by machine/date/file and by report export stage.
- Run expensive scans/copies off the UI thread.
- Keep cancellation-friendly code paths where practical.

## 18. Important Gotchas For Future Reviewers

- `OCR` and `AGING` prefixes are global ignored cell prefixes. If they appear in any production CSV feature queue, that is a regression.
- Production day means 06:00-to-06:00, not calendar midnight-to-midnight.
- Crop image drive should follow raw image drive. Blindly searching only `E` or scanning all `E/F/G` first causes slowdowns and misses real crops.
- `SEPA_SHOULDER` often appears as `SEPA_SHOULDER_DL` in production CSV judge columns.
- GAP, SEPA, HORNMARK, LEADEDGE, and other DLNG segmentation models must fetch only the side that actually matched the defect condition.
- HORNMARK/LEADEDGE file names have spacing variants. Matching cannot rely on one exact spelling.
- `B_DIM_L/R` intentionally creates two DLNG review pairs: HORNMARK and LEADEDGE.
- Queue navigation must use the displayed sorted view after the user clicks a grid column.
- Flag/unflag buttons must not live inside the zoomable image layer.
- `No Need to Retrain` / `No Need to Train` is a recorded decision but should not be exported into retraining datasets for segmentation after the Overkill addition.
- Kickout `Ignore` is a recorded decision but is excluded from summary counts and image saving.
- NG/Bypass rework duplicate rows stay reviewable and exportable, but summary counts only the first occurrence.
- IRS summary Rulebase output should be based on the currently loaded IRS list and should be line/polarity specific.
- DLNG summary/dataset generation should work from queued and reviewed cells only, not from all possible models in the date range.

## 19. Build, Test, And Publish

Use the solution file:

`C:\KWAK\6. Vision History\KickoutMonitor\KickoutMonitor.sln`

Common validation commands:

```powershell
dotnet test "C:\KWAK\6. Vision History\KickoutMonitor\KickoutMonitor.sln"
dotnet build "C:\KWAK\6. Vision History\KickoutMonitor\KickoutMonitor.sln" -c Release
```

When publishing `VisionMaster.exe`, close any running copy first. The single-instance mutex and Windows file locks can make launch/publish failures look like the executable starts a process without showing a usable window.

Before handing changes back, a reviewer should normally verify:

- Unit tests pass.
- Release build passes.
- Published executable launches.
- The changed feature can load a small queue.
- Keyboard shortcuts work for the changed review stage.
- Queue navigation still follows sorted display order.
- Summary generation creates the expected folder layout and preserves source filenames.

## 20. Recommended Change Approach

When adding or changing a feature:

1. Start in `VisionMasterSettings` and existing rule defaults if the behavior is configurable.
2. Add or adjust domain records for queue items/review records/report rows.
3. Put CSV/Excel/crop/raw path mechanics in Infrastructure.
4. Put queue filtering orchestration in Application services.
5. Keep WPF view models focused on commands, state, logs, progress, and binding-friendly collections.
6. Reuse existing IRS/DLNG/Kickout patterns for image navigation, queue coloring, review persistence, and report generation.
7. Add focused tests for CSV filtering, side selection, crop lookup, persistence reload, summary blocking, and output layout.

The most reliable mental model is: production data is a read-only source, review stores are the truth for operator decisions, and summary/dataset folders are generated artifacts that should be reproducible from source data plus review JSON.


## Overkill Monitor and training collection

See [Overkill Monitor workflow](Overkill_Monitor.md) for independent review/training selection, batch ownership and trained transitions, local history rates, Enter behavior, and flat SEPA exports.
