# Overkill Monitor and training workflow

## Review controls

Segmentation additionally offers **Not DLNG** with the **N** shortcut. It is a saved neutral judgment, neither Real nor Overkill; it is excluded from applicable DLNG statistics and training exports. Selecting it clears/disables training inclusion. With Auto advance enabled N saves/advances; otherwise it selects a draft for Enter/Save. Classification and raw fallback items do not receive this option.

DLNG final judgment and **Include in training dataset** are independent. New items start unselected for training. With Auto advance on, set the dataset checkbox before choosing a class. With Auto advance off, choose the class and dataset selection, then press Enter or Save. Revisiting a saved item restores both without arming a save.

Kickout likewise stages an explicit draft with Auto advance off. Enter or Save commits that draft and advances exactly one displayed item. With Auto advance on, choosing a judgment saves and advances once. Enter without an explicit draft does nothing. Held keys and concurrent commits are suppressed. Failed saves preserve the current item and draft. Dates, times, comments, and other editors do not route Enter to review commands.

Reviewing does not reorder queues. Explicit sorting and reloads may reorder groups. Rework grouping remains machine + Lot ID + cell ID, chronological within the admitted group. Judgment and image timestamps remain separate. Collection uses the resolved inspection/crop pair and never searches for a different attempt.

DLNG saved Real/NG classes are green, Overkill/OK classes red, pending/unknown labels neutral, and judgment-save failures amber. Dataset selection does not control color. The model picker has a bounded vertical scrollbar.

“No Need to Train/Retrain” is no longer a new judgment. Existing records remain unknown and are not automatically collected. Custom configured class choices remain available; unknown class semantics are excluded from rate denominators.

## Dashboard and history

Open **Overkill Monitor**, between LOG Monitor and Settings. Kickout and DLNG each show a machine line chart above a count heatmap. The shared default range is today and the preceding six dates, inclusive. Edit either date or use **최근 7일** to reset; refresh preserves the chosen range. Each tab remembers its own defect/crop selection.

Eight machine/polarity series use fixed colors and order, from 1-1(-) through 2-2(+). Legend checkboxes toggle the same machines in both views. Lines show daily overkill counts with a zero-based axis, straight segments and markers. Missing data breaks the line; a genuine zero has a marker at zero. Hover a marker for date, machine, field and count. Click for records; coincident markers offer a machine menu.

Heatmap rows are machines and columns are all known defects/crop models. Cells sum the selected period, with a common linear 0-to-maximum count scale across visible cells. Pale zero and gray missing (—) are distinct. Tooltips report counts and covered days. Click a column header or cell count to select that field for the chart without hiding other columns or machines; **⋯** opens that machine/field's records across the period. The detail panel closes to return space to the chart. Long heatmaps scroll horizontally; smaller windows allow vertical scrolling.

Kickout retains generated summaries' production days and counting rules. **전체** uses only ALL rows, never a sum of defect rows. An absent defect in a complete reported day is zero; a missing report is a gap. New summaries persist snapshots and review details under OverkillHistory. Refresh imports recognized NG_Summary workbooks; regeneration replaces a day/line snapshot instead of accumulating duplicates. Details distinguish report aggregates from individual review records; these are not additive. Legacy summaries may have only aggregate provenance.

DLNG is labeled **리뷰된 샘플 기준**. Production day is the date of inspection time minus six hours: 05:59:59 belongs to the preceding day, 06:00 to the current day. Review-save time and training selection do not affect these counts. Classification NG → OK is overkill; NG → different NG and OK → NG are valid reviews but not overkill. Segmentation uses Real/Overkill. Unknown/legacy “No Need” labels contribute neither overkill nor valid-review coverage. No valid review for a machine/field/day is missing; valid reviews without overkill give zero. Inspection-and-crop identity deduplicates representations while retaining separate rework attempts.

Product filters/columns, ranked counts, daily tables and main-screen rates are removed. Internal E81C/E69B identity and separation remain unchanged. No stored records are migrated or reclassified by this dashboard. Newly collected sample counts still use successful collection dates, and batch history remains accessible regardless of the range. Local history works without production-share access.

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

Additional trend tests cover inclusive seven-day ranges, month/year transitions, the DLNG 06:00 boundary, ALL non-double-counting, null versus zero, partial coverage, shared linear colors, rework identities and training-independent counts.

WPF smoke checks cover the chart/matrix, field clicks, legend visibility, detail opening/closing, per-tab selections, preserved refresh ranges, date reset and invalid ranges, plus six views at 1100×700, explicit drafts, Enter and auto advance, held/concurrent activation, failed saves, restored selections, editor guards, saved-class semantics, grouped sorting, navigation and flags.

## Queue defaults and SEPA additions (2026-09-23)

- DLNG starts at yesterday 06:00 and ends at the current local date/time when the view model is initialized.
- Kickout before 06:30 defaults to yesterday 06:00–today 06:00; at/after 06:30 it defaults to today 06:00–tomorrow 06:00. Report dates and report windows are unchanged.
- SS_Left, SS_Right, Tab_Burr_LL, Tab_Burr_LB, Tab_Burr_RR, and Tab_Burr_RB also target SEPA and SEPA_SHOULDER crops when those models are selected. Existing Tab Burr classification mappings remain available. This supplement works with previously saved settings without resetting them.
- Existing NG/DLNG eligibility, affected upper/lower side selection, exact inspection ownership, and time filtering still apply. LM/RM were not added to the supplemental segmentation targets.
- The DLNG toolbar remains one row; model selection takes less horizontal space and date/time fields and action buttons are larger.
