# Rework-safe inspection review

An inspection is one production judgment with its own image evidence. Cell ID alone is never sufficient to select images.

## Judgment time and image time

Queues display the full CSV judgment time. Crop lookup uses the inclusive interval between the CSV judgment time and the timestamp encoded in that row's explicit raw/overlay filenames, at filename second precision. The supplied fixture for cell D69GX05JJ1, lot 3A4FI161I1, contains judgment/image times 11:13:03/11:13:03 and 11:36:26/11:36:27. Both are eligible upper SEPA inspections; each retrieves only its own upper pair.

Inspection context retains machine, model, lot, cell, judgment time, image time, all explicit raw/overlay paths, CSV path and row. Production raw filenames may use either the standard cell_side_camera suffix or cell_EXT_side_camera suffix; EXT is optional and is not part of inspection identity validation. Conflicting image evidence stays unresolved. IRS and Flagged recovery resolve exact filenames or exact timestamp context against CSV records; multiple matches are ambiguous. There is no nearest-time or cell-only fallback.

Mavin lookup follows the original image drive and model, searching the calendar dates touched by the inspection interval. Crop matching requires the exact cell and line/polarity, a timestamp inside that interval, the selected side and existing model/defect rules. Source/overlay and source/mask pairs share a complete crop stem. Missing crops can use the selected inspection's raw paths; unresolved inspections cannot borrow raw images from another attempt. Queue reloads refresh lookup indexes.

## Queue behavior

Eligibility is evaluated first. Kickout, DLNG, NG/Bypass, IRS and Flagged group only admitted rows by machine + Lot ID + cell ID. Missing lots remain ungrouped. Known lots can cross midnight without splitting the group. Grouping never fetches additional attempts or expands the selected time range.

Attempts remain separate, adjacent and chronological, with each attempt's side/crop rows together. The subtle inputs label counts distinct inspections, not crop pairs. Existing review-status priority applies to entire groups. Column sorting compares the first chronological entry of each group; keyboard navigation follows the displayed order.

NG/Bypass's existing first-occurrence summary count includes Lot ID. Other summary counting rules and report windows remain unchanged. LOG Monitor retry analysis is unchanged.

## Saved reviews and output ownership

Stable inspection-and-crop keys replace enumeration positions. Duplicate representations collapse; distinct image evidence at the same judgment time remains separate. Adding another pair or sorting a queue does not transfer a review decision.

Local preview, review-copy and export directories include an inspection/item ownership hash. Established image filenames remain intact. Reclassification only removes the selected item's owned outputs. Repeated dataset generation is idempotent.

Stores are backed up to a sibling .rework-v1.bak before migration/writes. Legacy records are retained. Legacy DLNG crop decisions are reused only when stored paths establish the pair. Unverifiable IRS positional decisions require review again. Legacy IRS/Flagged saved images must match the resolved inspection before inclusion in newly generated datasets; mixed or ambiguous results remain unresolved. Legacy shared folders are not deleted as part of another item's reclassification.

## Local validation

The minimal test fixture is KickoutMonitor/tests/KickoutMonitor.Tests/Fixtures/rework.csv. The original DataSample files and production sources remain read-only.

From the repository root:

    dotnet test KickoutMonitor/KickoutMonitor.sln -c Release --no-restore -p:UseSharedCompilation=false -m:1 -nr:false
    dotnet build KickoutMonitor/KickoutMonitor.sln -c Release --no-restore -p:UseSharedCompilation=false -m:1 -nr:false
    dotnet run --project KickoutMonitor/tests/KickoutMonitor.UiSmoke/KickoutMonitor.UiSmoke.csproj -c Release --no-restore -p:UseSharedCompilation=false

The WPF harness uses temporary local files and fake services. It constructs/layouts all five views and checks grouping, repeat counts, sorting, Up/Down navigation, previews and flag context without running production startup. Automated fixtures cover the sample offset, eligibility, duplicate rows, multiple attempts, lot/machine boundaries, midnight image dates, classification/segmentation pairing, missing/conflicting/ambiguous data, reload/cancellation, stable decisions and isolated exports. Live production-share behavior still depends on source availability.

## Diagnosing crop fallbacks

The DLNG Images column reports the actual lookup stage: missing/unavailable folder, access/read failure, no crops for the cell, machine/capture-time mismatch, side mismatch, defect mismatch, or no usable source crop. Hover over the status to see its complete text; queue activity also records the reason. Timestamp mismatches show the expected inspection interval and up to eight distinct crop times found for that cell in the bounded search roots.

The September 20 B sample established that crop timestamps can be at judgment time, raw-image time, or between them. Its 4,116 CSV inspections all match within their judgment-to-raw interval; upper and lower crops can have different timestamps. Matching is bounded by this observed interval, never nearest-time selection.

Ownership checks index all CSV rows, including excluded OK attempts and other lots, across daily continuation files and neighboring days. Neighboring rows are used only to reject overlapping ownership; they do not enter the queue or change report windows. Each crop's exact timestamp must belong to only one inspection. Duplicate representations of the same inspection do not block it. Source/overlay or source/mask pairing retains the complete filename stem including the crop timestamp.

The same matching rule is used for DLNG, IRS/Flagged crop recovery and saved-image validation. If surrounding inspection context cannot be checked, only the prior exact raw timestamp rule is available. Missing folder and missing file results are not evidence of timestamp mismatch.

The shared raw resolver accepts LOWER as well as BTM/BOTTOM for the lower camera. Kickout and NG/Bypass continue to use explicit CSV raw paths; their normal previews do not depend on crop timestamp lookup.

## Queue time controls

Kickout and DLNG queue loading accepts start/end times beside the existing dates. Defaults are 06:00 and 06:00, with 1-1(-), 1-1(+), 1-2(-), and 1-2(+) selected. Date defaults are unchanged: Kickout yesterday/today, DLNG today/today. Consequently DLNG requires an end date/time later than its start before loading.

Queue filtering uses the full CSV judgment timestamp: start included, end excluded. It does not filter by raw/crop timestamp. CSV discovery still uses calendar dates; an end at midnight does not request that new date. DLNG ownership checks still include excluded inspections so the time filter cannot hide crop ambiguity. Records outside the range are not cached or expanded into crop pairs.

Invalid times or reversed/equal ranges show a message before IO or clearing the current queue. A valid range with no matches shows an empty queue. Report date controls and date-only service calls keep their existing behavior.
