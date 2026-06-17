from pathlib import Path
from tempfile import TemporaryDirectory

from openpyxl import load_workbook

from vision_tracker import (
    IssueInput,
    create_issue,
    export_issues_to_excel,
    initialize_database,
    resolve_issue,
    search_issues,
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

        rows = search_issues({"status": "Open", "category": "Hardware"}, db_path)
        assert len(rows) == 1
        assert rows[0]["title"] == "Camera disconnect during inspection"

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
            ),
            db_path,
        )
        resolve_issue(issue_id_2, db_path=db_path)
        grab_fail = search_issues({"category": "Camera Grab Fail"}, db_path)
        assert len(grab_fail) == 1
        assert grab_fail[0]["status"] == "Resolved"


if __name__ == "__main__":
    run_tests()
    print("All tests passed.")
