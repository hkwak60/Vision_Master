from pathlib import Path
from tempfile import TemporaryDirectory

from openpyxl import load_workbook

from vision_tracker import (
    IssueInput,
    active_issues,
    create_issue,
    dashboard_counts,
    delete_issue,
    export_issues_to_excel,
    initialize_database,
    resolve_issue,
    search_issues,
    set_issue_status,
    update_issue,
)


def run_tests() -> None:
    with TemporaryDirectory() as temp_dir:
        db_path = Path(temp_dir) / "test.db"
        initialize_database(db_path)

        issue_id = create_issue(
            IssueInput(
                issue_time="2026-06-17 08:10",
                line="1-1",
                instrument="Pinhole",
                worker="Hojun Kwak",
                category="Hardware",
                subcategory="Camera",
                title="Camera disconnect during inspection",
                description="Camera stopped responding during production.",
            ),
            db_path,
        )
        assert issue_id == 1

        rows = search_issues({"status": "Action Required", "category": "Hardware"}, db_path)
        assert len(rows) == 1
        assert rows[0]["title"] == "Camera disconnect during inspection"
        active = active_issues(db_path)
        assert len(active) == 1
        counts = dashboard_counts(db_path)
        assert counts["Action Required"] == 1
        assert counts["Active"] == 1

        update_issue(
            issue_id,
            IssueInput(
                issue_time="2026-06-17 08:10",
                resolved_time="2026-06-17 08:42",
                line="1-1",
                instrument="Pinhole",
                worker="Hojun Kwak",
                category="Hardware",
                subcategory="Camera",
                title="Camera disconnect during inspection",
                description="Camera stopped responding during production.",
                status="Resolved",
                resolution_notes="Reconnected camera cable and restarted program.",
            ),
            db_path,
        )

        resolved = search_issues({"status": "Resolved"}, db_path)
        assert len(resolved) == 1

        export_path = Path(temp_dir) / "report.xlsx"
        export_issues_to_excel(resolved, export_path)
        workbook = load_workbook(export_path)
        sheet = workbook.active
        assert sheet["B2"].value == "2026-06-17 08:10"
        assert sheet["I2"].value == "Camera disconnect during inspection"
        assert sheet["C1"].value == "Downtime Duration"
        assert sheet["F1"].value == "Logged By"

        issue_id_2 = create_issue(
            IssueInput(
                issue_time="2026-06-17 09:00",
                line="1-2",
                instrument="Lead",
                worker="Kijung Kim",
                category="Camera Grab Fail",
                subcategory="",
                title="Grab timeout",
                description="Camera failed to grab during cycle.",
                status="Monitoring",
            ),
            db_path,
        )
        resolve_issue(issue_id_2, db_path=db_path)
        grab_fail = search_issues({"category": "Camera Grab Fail"}, db_path)
        assert len(grab_fail) == 1
        assert grab_fail[0]["status"] == "Resolved"

        set_issue_status(issue_id, "Monitoring", db_path)
        monitoring = search_issues({"status": "Monitoring"}, db_path)
        assert len(monitoring) == 1

        issue_id_3 = create_issue(
            IssueInput(
                issue_time="2026-06-17 10:00",
                line="2-1",
                instrument="Sealing",
                worker="Jihoon Yun",
                category="Recipe",
                subcategory="Overkill",
                title="Overkill trend",
                description="Reject rate increased after recipe change.",
            ),
            db_path,
        )
        delete_issue(issue_id_3, db_path)
        deleted = search_issues({"keyword": "Overkill trend"}, db_path)
        assert len(deleted) == 0


if __name__ == "__main__":
    run_tests()
    print("All tests passed.")
