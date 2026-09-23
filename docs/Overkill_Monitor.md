# Overkill Monitor and training workflow

## Review controls

DLNG final judgment and **Include in training dataset** are independent. New items start unselected for training. With Auto advance on, set the dataset checkbox before choosing a class. With Auto advance off, choose the class and dataset selection, then press Enter or Save. Revisiting a saved item restores both without arming a save.

Kickout likewise stages an explicit draft with Auto advance off. Enter or Save commits that draft and advances exactly one displayed item. With Auto advance on, choosing a judgment saves and advances once. Enter without an explicit draft does nothing. Held keys and concurrent commits are suppressed. Failed saves preserve the current item and draft. Dates, times, comments, and other editors do not route Enter to review commands.

Reviewing does not reorder queues. Explicit sorting and reloads may reorder groups. Rework grouping remains machine + Lot ID + cell ID, chronological within the admitted group. Judgment and image timestamps remain separate. Collection uses the resolved inspection/crop pair and never searches for a different attempt.

DLNG saved Real/NG classes are green, Overkill/OK classes red, pending/unknown labels neutral, and judgment-save failures amber. Dataset selection does not control color. The model picker has a bounded vertical scrollbar.

“No Need to Train/Retrain” is no longer a new judgment. Existing records remain unknown and are not automatically collected. Custom configured class choices remain available; unknown class semantics are excluded from rate denominators.

## Dashboard and history

Open **Overkill Monitor**, between LOG Monitor and Settings. Filter by date, product, line/polarity, and defect/crop model. Select a ranked row, then open **Contributing records**.

Kickout uses generated summaries with their established production-day windows and counting rules. It shows overkill / inspected and overkill / reviewed rejects, a heatmap, ranked counts, and daily trends. Multi-day rates use summed counts. Missing report days are gaps. Reported days without an occurrence of a defect still contribute their inspected denominator. Overall totals use ALL rows to avoid repeating per-defect inspected counts.

New summaries persist local snapshots and individual review details under OverkillHistory. Refresh imports recognized existing NG_Summary workbooks. Regenerating a day/line replaces its complete snapshot instead of accumulating duplicates. Legacy workbooks without product evidence appear as **Unknown**. Legacy drill-down identifies the contributing report and aggregate counts; new snapshots also show individual review details.

DLNG statistics describe **selectively reviewed samples, not production-wide rates**. Classification NG → OK is overkill; NG → a different NG class is a class correction; OK → NG is a missed defect. Segmentation uses Real/Overkill. Applicable reviews count regardless of dataset selection. Unknown labels appear separately and are excluded from rate denominators. Duplicate representations use inspection-and-crop identities for deduplication.

Review metrics use inspection dates; newly collected sample counts use successful collection dates. Batch history remains available outside the date filter for copy recovery and trained-state management.

## Training batches

A complete source/overlay or source/mask pair is **one sample**. Raw fallback reviews cannot become training crops. Review decisions are saved before copying. Missing/incomplete pairs remain failed or pending and do not increase collected counts.

Local layout:

    Training/<product>/<crop model>/<polarity or shared>/<year>/<stable batch ID>/<mmdd_mmdd>/<class>/

Classification pools lines within product + crop model + polarity. Segmentation pools lines and polarities within product + crop model. E81C and E69B stay separate. Date names use first/latest successful collection dates; year and stable ID distinguish identical date ranges and year crossings.

There is one active batch per grouping. **Retry pending copies** replays explicit selections. **Exclude selected sample** persists deselection and removes only that sample's owned active files. **Mark selected batch trained** freezes the batch and removes its samples from new counts; pending/failed copies must first be retried or excluded. The next new selected sample starts another batch. Later corrections flag trained samples as superseded without altering frozen files. Exporting does not mark a batch trained.

Active reclassification uses local owned images when available, works offline, and retains the original collection date. SEPA pairs sit directly under Real or Overkill, with a shared deterministic product/inspection/crop prefix followed by the original filename. There are no per-item subfolders below these class folders. Other models retain per-sample collection directories.

## Reports, exports, and recovery

Reports describe all saved decisions, including unselected and legacy unknown labels. Both DLNG dataset paths export only explicitly selected, successfully collected pairs using local files. Both keep SEPA flat under Real/Overkill. Other models retain established class/report naming layouts.

Export ownership manifests support idempotent exports and removal/reclassification of only the selected item's files. Unrelated files are retained. Missing local pairs are excluded from export.

DLNG records persist training selection and timestamps independently of class. Training/batches.json is the authoritative ledger for copy state, paths, labels, dates, and trained/superseded status. Copy intent is journaled before file changes; pairs stage locally before publication. Retries recover interrupted copies and range moves without adding duplicate samples.

Existing DLNG stores receive an .overkill-v1.bak before their first write in this workflow. Prior rework backups remain intact. History and collection manifests are also backed up before replacement. History remains available offline, and historical images are never collected automatically. Production CSV/image sources remain read-only.

## Validation

Release tests cover rate denominators and unknown labels; report gaps, zero-defect days, imports and replacement; explicit selection; pair completeness and ownership; grouping, year crossings and trained transitions; offline reclassification and both exports; interrupted staging/moves, locked sources, cancellation and safe deselection.

WPF smoke checks cover six views at 1100×700, explicit drafts, Enter and auto advance, held/concurrent activation, failed saves, restored selections, editor guards, saved-class semantics, grouped sorting, navigation and flags.

## Queue defaults and SEPA additions (2026-09-23)

- DLNG starts at yesterday 06:00 and ends at the current local date/time when the view model is initialized.
- Kickout before 06:30 defaults to yesterday 06:00–today 06:00; at/after 06:30 it defaults to today 06:00–tomorrow 06:00. Report dates and report windows are unchanged.
- SS_Left, SS_Right, Tab_Burr_LL, Tab_Burr_LB, Tab_Burr_RR, and Tab_Burr_RB also target SEPA and SEPA_SHOULDER crops when those models are selected. Existing Tab Burr classification mappings remain available. This supplement works with previously saved settings without resetting them.
- Existing NG/DLNG eligibility, affected upper/lower side selection, exact inspection ownership, and time filtering still apply. LM/RM were not added to the supplemental segmentation targets.
- The DLNG toolbar remains one row; model selection takes less horizontal space and date/time fields and action buttons are larger.
