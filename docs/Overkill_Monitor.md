# Overkill Monitor and training workflow

## Review controls

Segmentation additionally offers **Not DLNG** with the **N** shortcut. It is a saved neutral judgment, neither Real nor Overkill; it is excluded from applicable DLNG statistics and training exports. Selecting it clears/disables training inclusion. With Auto advance enabled N saves/advances; otherwise it selects a draft for Enter/Save. Classification and raw fallback items do not receive this option.

DLNG final judgment and **Include in training dataset** remain separate. New items start unchecked; choosing **Overkill for a segmentation model** defaults to collection. Other judgments default off unless the current item was explicitly selected with **T** or the checkbox. An explicit checkbox override wins for that item. **T** selects inclusion only for the current item and does not carry to the next. Revisiting a saved item restores its persisted inclusion flag.

With Auto advance on, choosing a judgment saves and advances. With it off, Enter or Save commits an explicitly edited judgment/training selection. Enter on an already saved item with no edits advances to the next item in the current displayed order, including column sorting. Enter on a pending item without a selected judgment does nothing. Failed saves retain the draft. The review toolbar stays on one row. Lines use a fixed 4-column × 2-row selector; Models is capped at roughly one quarter of the available width and shrinks further in small windows, leaving flexible room for dates and buttons. Queue columns are Date, Time, Line, Cell-ID, Rework, Judge, Defect, Side, Final Class, Review; image-resolution details remain in row tooltips.

Kickout likewise stages an explicit draft with Auto advance off. Enter or Save commits that draft and advances exactly one displayed item. With Auto advance on, choosing a judgment saves and advances once. Enter without an explicit draft does nothing. Held keys and concurrent commits are suppressed. Failed saves preserve the current item and draft. Dates, times, comments, and other editors do not route Enter to review commands.

Reviewing does not reorder queues. Explicit sorting and reloads may reorder groups. Rework grouping remains machine + Lot ID + cell ID, chronological within the admitted group. Judgment and image timestamps remain separate. Collection uses the resolved inspection/crop pair and never searches for a different attempt.

DLNG saved Real/NG classes are green, Overkill/OK classes red, pending/unknown labels neutral, and judgment-save failures amber. Dataset selection does not control color. The model picker has a bounded vertical scrollbar.

“No Need to Train/Retrain” is no longer a new judgment. Existing records remain unknown and are not automatically collected. Custom configured class choices remain available; unknown class semantics are excluded from rate denominators.

## Dashboard and history

Open **Overkill Monitor**, between LOG Monitor and Settings. Kickout and DLNG each display **four line rows**, in fixed order 1-1, 1-2, 2-1, 2-2. Negative and positive polarity cards sit side by side within each row, each with its own daily graph and narrow vertical count table. The graph/table height remains 215 pixels; tables use 140 pixels so most horizontal space stays with graphs. Scroll the page to reach later machines. The shared range defaults to today and the preceding six dates, inclusive. Refresh preserves it; **최근 7일** resets it. Each tab remembers its field selection.

The field list is ordered by descending total overkill count; each machine's table is ordered by its own period total, descending, with missing data after actual zero. Clicking a table field selects that field for the charts without removing other fields. Legend checkboxes hide/show whole machine rows. Graphs retain fixed colors, zero-based axes, straight segments and markers; missing days break lines and real zeros remain marked. Hover/click points for counts and detail records. Overlapping points remain individually accessible.

Tables retain a shared linear count color scale across visible machines. Pale zero and gray missing (—) remain distinct, and tooltips show covered days. **⋯** opens that machine/field's period records in the closable detail panel.

Kickout retains generated summaries' production days and counting rules. **전체** uses only ALL rows, never a sum of defect rows. An absent defect in a complete reported day is zero; a missing report is a gap. New summaries persist snapshots and review details under OverkillHistory. Refresh imports recognized NG_Summary workbooks; regeneration replaces a day/line snapshot instead of accumulating duplicates. Details distinguish report aggregates from individual review records; these are not additive. Legacy summaries may have only aggregate provenance.

DLNG is labeled **리뷰된 샘플 기준**. Production day is the date of inspection time minus six hours: 05:59:59 belongs to the preceding day, 06:00 to the current day. Review-save time and training selection do not affect these counts. Classification NG → OK is overkill; NG → different NG and OK → NG are valid reviews but not overkill. Segmentation uses Real/Overkill. Unknown/legacy “No Need” labels contribute neither overkill nor valid-review coverage. No valid review for a machine/field/day is missing; valid reviews without overkill give zero. Inspection-and-crop identity deduplicates representations while retaining separate rework attempts.

Product filters/columns and main-screen rates are hidden. Internal E81C/E69B identity and separation remain unchanged. No stored records are migrated or reclassified by this dashboard. Newly collected sample counts still use successful collection dates, and batch history remains accessible regardless of the range. Local history works without production-share access.

## Training batches

A complete source/overlay or source/mask pair is **one sample**. Raw fallback reviews cannot become training crops. Review decisions are saved before copying. Missing/incomplete pairs remain failed or pending and do not increase collected counts.

Local layout:

    Training/<product>/<crop model>/<polarity or shared>/<year>/<stable batch ID>/<mmdd_mmdd>/<class>/

Classification pools lines within product + crop model + polarity. Segmentation pools lines and polarities within product + crop model. E81C and E69B stay separate. Date names use first/latest successful collection dates; year and stable ID distinguish identical date ranges and year crossings.

There is one active batch per grouping. **Retry pending copies** replays explicit selections. **Exclude selected sample** persists deselection and removes only that sample's owned active files. **Generate Dataset** in Training collection replaces Mark trained and the DLNG Review dataset button. It exports the selected active batch from local collected pairs, verifies the complete output, then freezes the batch and removes it from newly collected counts. Pending/failed copies must first be retried or excluded. A copy failure/cancellation leaves the batch active. The next successful new collection starts a fresh batch. Later corrections flag trained samples as superseded without altering frozen files.

Dataset layout:

    DLNG_REPORT/DATASET/<product>/<crop>/<polarity or shared>/<first-image year>/<batch ID>/<mmdd_mmdd>/<class>/

The range uses earliest/latest image dates (inspection dates if image timestamps are unavailable), independently of collection dates. All segmentation source/mask pairs are exported directly under Real or Overkill with deterministic ownership prefixes. Classification models retain sample subfolders. Previously exported trained segmentation batches can use Generate Dataset again to create a verified flat copy under <batch ID>/flat/<date range>; frozen originals remain intact and the dataset path is updated. A dataset manifest records identities, labels, relative paths and file hashes; publication is atomic and retrying after an interrupted trained-state write reuses the complete output. The generated path appears in the batch table and status. Collection exports now mark trained; legacy report attachments do not.

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

Kickout queue hides the Images column; underlying image resolution behavior is unchanged.
