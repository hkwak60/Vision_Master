from __future__ import annotations

import calendar
import os
import sys
from datetime import datetime, timedelta
from pathlib import Path


if getattr(sys, "frozen", False):
    tcl_root = Path(getattr(sys, "_MEIPASS", Path(sys.executable).parent)) / "tcl"
    os.environ.setdefault("TCL_LIBRARY", str(tcl_root / "tcl8.6"))
    os.environ.setdefault("TK_LIBRARY", str(tcl_root / "tk8.6"))

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from vision_tracker import (
    ACTIVE_STATUS_OPTIONS,
    APP_TITLE,
    CATEGORIES,
    CATEGORY_MAP,
    INSTRUMENTS,
    INSTRUMENT_GROUP,
    LINES,
    STATUS_OPTIONS,
    VERSION_GROUPS,
    WORKERS,
    IssueInput,
    VersionInput,
    active_issues,
    create_version_update,
    create_issue,
    dashboard_counts,
    delete_issue,
    export_issues_to_excel,
    format_instruments,
    get_issue,
    initialize_database,
    issue_time_bounds,
    latest_version_by_instrument,
    now_text,
    recent_version_templates,
    resolve_issue,
    search_issues,
    set_issue_status,
    split_instruments,
    update_issue,
)


STATUS_TAGS = {
    "Action Required": ("#fff2f0", "#9f1239"),
    "Monitoring": ("#fff8db", "#854d0e"),
    "Resolved": ("#ecfdf3", "#166534"),
}

LANGUAGES = ["English", "한국어"]
VERSION_TAB_SPACER = " " * 80
TRANSLATIONS = {
    "한국어": {
        "Current Worker": "작업자",
        "Language": "언어",
        "Open Issues": "미해결 이슈",
        "New / Edit Issue": "이슈 등록 / 수정",
        "Search & Excel Report": "검색 및 엑셀 보고서",
        "Version History": "버전 기록",
        "Action Required": "조치 필요",
        "Monitoring": "모니터링",
        "Resolved": "해결됨",
        "Resolved Today": "오늘 해결",
        "Active": "진행 중",
        "Refresh": "새로고침",
        "Edit": "수정",
        "Delete": "삭제",
        "Selected Issue": "선택된 이슈",
        "Title": "제목",
        "Status": "상태",
        "Line / Instrument": "라인 / 비전",
        "Category": "분류",
        "Issue Time": "발생 시간",
        "Logged By": "작성자",
        "Downtime Duration": "다운타임",
        "Description": "설명",
        "Issue Record": "이슈 기록",
        "Line": "라인",
        "Vision": "비전",
        "Subcategory": "세부 분류",
        "Resolution Notes": "조치 내용",
        "New": "새 기록",
        "Save": "저장",
        "ID": "번호",
        "Instrument": "비전",
        "Worker": "작업자",
        "Keyword": "키워드",
        "From": "시작",
        "To": "종료",
        "Today": "오늘",
        "This Week": "이번 주",
        "Camera Grab Fail": "카메라 Grab 실패",
        "Recipe Issues": "레시피 이슈",
        "Clear": "초기화",
        "Search": "검색",
        "Excel": "엑셀",
        "Vision Filter": "비전 필터",
        "Version Dashboard": "버전 대시보드",
        "Version Update": "버전 업데이트",
        "Version Group": "버전 그룹",
        "Version Template": "버전 템플릿",
        "SW Version": "SW 버전",
        "Algo Version": "Algo 버전",
        "Update Time": "업데이트 시간",
        "Save Version Update": "버전 업데이트 저장",
        "Create Monitoring Issue": "모니터링 이슈 등록",
        "Version Description": "버전 설명",
        "Group": "그룹",
    }
}


class VisionIssueApp(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        initialize_database()
        self.title(APP_TITLE)
        self.geometry("1180x740")
        self.minsize(980, 620)
        self.selected_issue_id: int | None = None
        self.loaded_issue_worker = ""
        self.search_rows = []
        self.language_var = tk.StringVar(value="한국어")
        self.translated_widgets: list[tuple[tk.Widget, str, str]] = []

        self.configure(bg="#f4f6f8")
        self.style = ttk.Style(self)
        self.style.theme_use("clam")
        self.configure_styles()

        self.build_layout()
        self.refresh_open_issues()
        self.search_records()

    def configure_styles(self) -> None:
        self.style.configure("TNotebook", background="#f4f6f8", borderwidth=0)
        self.style.configure("TNotebook.Tab", padding=(18, 10), font=("Segoe UI", 10, "bold"))
        self.style.map("TNotebook.Tab", background=[("disabled", "#ffffff")], foreground=[("disabled", "#ffffff")])
        self.style.configure("TFrame", background="#f4f6f8")
        self.style.configure("Panel.TFrame", background="#ffffff", relief="solid", borderwidth=1)
        self.style.configure("TLabel", background="#f4f6f8", font=("Segoe UI", 10))
        self.style.configure("Panel.TLabel", background="#ffffff", font=("Segoe UI", 10))
        self.style.configure("Header.TLabel", background="#f4f6f8", font=("Segoe UI", 18, "bold"))
        self.style.configure("Subheader.TLabel", background="#ffffff", font=("Segoe UI", 12, "bold"))
        self.style.configure("TButton", font=("Segoe UI", 10), padding=(12, 7))
        self.style.configure("Accent.TButton", font=("Segoe UI", 10, "bold"), padding=(12, 7))
        self.style.configure("Treeview", font=("Segoe UI", 9), rowheight=28)
        self.style.configure("Treeview.Heading", font=("Segoe UI", 9, "bold"))
        self.style.configure("Card.TFrame", background="#ffffff", relief="solid", borderwidth=1)
        self.style.configure("CardTitle.TLabel", background="#ffffff", font=("Segoe UI", 9))
        self.style.configure("CardValue.TLabel", background="#ffffff", font=("Segoe UI", 20, "bold"))

    def build_layout(self) -> None:
        header = ttk.Frame(self, padding=(18, 14, 18, 8))
        header.pack(fill="x")
        ttk.Label(header, text=APP_TITLE, style="Header.TLabel").pack(side="left")
        profile = ttk.Frame(header)
        profile.pack(side="right")
        self.tr_label(profile, "Language").pack(side="left", padx=(0, 8))
        language_combo = ttk.Combobox(
            profile,
            textvariable=self.language_var,
            values=LANGUAGES,
            state="readonly",
            width=10,
        )
        language_combo.pack(side="left", padx=(0, 18))
        language_combo.bind("<<ComboboxSelected>>", self.apply_language)
        self.tr_label(profile, "Current Worker").pack(side="left", padx=(0, 8))
        self.current_worker_var = tk.StringVar(value=WORKERS[0])
        ttk.Combobox(
            profile,
            textvariable=self.current_worker_var,
            values=WORKERS,
            state="readonly",
            width=20,
        ).pack(side="left")

        self.notebook = ttk.Notebook(self)
        self.notebook.pack(fill="both", expand=True, padx=18, pady=(0, 18))

        self.open_tab = ttk.Frame(self.notebook, padding=14)
        self.entry_tab = ttk.Frame(self.notebook, padding=14)
        self.search_tab = ttk.Frame(self.notebook, padding=14)
        self.version_spacer_tab = ttk.Frame(self.notebook)
        self.version_tab = ttk.Frame(self.notebook, padding=14)
        self.notebook.add(self.open_tab, text="Open Issues")
        self.notebook.add(self.entry_tab, text="New / Edit Issue")
        self.notebook.add(self.search_tab, text="Search & Excel Report")
        self.notebook.add(self.version_spacer_tab, text=VERSION_TAB_SPACER, state="disabled")
        self.notebook.add(self.version_tab, text="Version History")

        self.build_open_tab()
        self.build_entry_tab()
        self.build_search_tab()
        self.build_version_tab()
        self.apply_language()

    def text(self, key: str) -> str:
        return TRANSLATIONS.get(self.language_var.get(), {}).get(key, key)

    def register_text(self, widget: tk.Widget, key: str, option: str = "text") -> tk.Widget:
        self.translated_widgets.append((widget, key, option))
        widget.configure(**{option: self.text(key)})
        return widget

    def tr_label(self, parent: tk.Widget, key: str, **kwargs) -> ttk.Label:
        label = ttk.Label(parent, **kwargs)
        return self.register_text(label, key)

    def tr_button(self, parent: tk.Widget, key: str, command, prefix: str = "", **kwargs) -> ttk.Button:
        button = ttk.Button(parent, command=command, **kwargs)
        self.translated_widgets.append((button, key, "text"))
        button.configure(text=f"{prefix}{self.text(key)}")
        button.translation_prefix = prefix
        return button

    def apply_language(self, _event: tk.Event | None = None) -> None:
        for widget, key, option in self.translated_widgets:
            prefix = getattr(widget, "translation_prefix", "")
            widget.configure(**{option: f"{prefix}{self.text(key)}"})
        if hasattr(self, "notebook"):
            self.notebook.tab(self.open_tab, text=self.text("Open Issues"))
            self.notebook.tab(self.entry_tab, text=self.text("New / Edit Issue"))
            self.notebook.tab(self.search_tab, text=self.text("Search & Excel Report"))
            self.notebook.tab(self.version_spacer_tab, text=VERSION_TAB_SPACER)
            self.notebook.tab(self.version_tab, text=self.text("Version History"))
        for tree_name in ["open_tree", "search_tree"]:
            if hasattr(self, tree_name):
                self.update_tree_headings(getattr(self, tree_name))
        if hasattr(self, "version_create_issue_button"):
            self.refresh_create_issue_button()

    def update_tree_headings(self, tree: ttk.Treeview) -> None:
        headings = {
            "id": "ID",
            "issue_time": "Issue Time",
            "line": "Line",
            "instrument": "Instrument",
            "category": "Category",
            "subcategory": "Subcategory",
            "title": "Title",
            "status": "Status",
            "worker": "Logged By",
        }
        for column, key in headings.items():
            tree.heading(column, text=self.text(key))

    def build_open_tab(self) -> None:
        self.dashboard_frame = ttk.Frame(self.open_tab)
        self.dashboard_frame.pack(fill="x", pady=(0, 12))
        self.dashboard_vars: dict[str, tk.StringVar] = {}
        for title in ["Action Required", "Monitoring", "Resolved Today", "Active"]:
            self.add_dashboard_card(self.dashboard_frame, title)

        toolbar = ttk.Frame(self.open_tab)
        toolbar.pack(fill="x", pady=(0, 10))
        self.tr_button(toolbar, "Refresh", self.refresh_open_issues, prefix="↻ ").pack(side="left")
        self.tr_button(toolbar, "Edit", self.load_selected_open_issue, prefix="✎ ").pack(side="left", padx=8)
        self.tr_button(toolbar, "Resolved", self.resolve_selected_open_issue, prefix="✓ ").pack(side="left")
        self.tr_button(toolbar, "Delete", lambda: self.delete_selected_issue(self.open_tree), prefix="✕ ").pack(side="left", padx=8)

        content = ttk.Frame(self.open_tab)
        content.pack(fill="both", expand=True)
        content.columnconfigure(0, weight=3)
        content.columnconfigure(1, weight=1)
        content.rowconfigure(0, weight=1)

        open_table = ttk.Frame(content)
        open_table.grid(row=0, column=0, sticky="nsew", padx=(0, 12))
        open_table.columnconfigure(0, weight=1)
        open_table.rowconfigure(0, weight=1)
        self.open_tree = self.make_issue_tree(open_table)
        self.open_tree.grid(row=0, column=0, sticky="nsew")
        open_scroll = ttk.Scrollbar(open_table, orient="vertical", command=self.open_tree.yview)
        open_scroll.grid(row=0, column=1, sticky="ns")
        self.open_tree.configure(yscrollcommand=open_scroll.set)
        self.open_tree.bind("<Double-1>", lambda _event: self.load_selected_open_issue())
        self.open_tree.bind("<<TreeviewSelect>>", lambda _event: self.update_detail_panel(self.open_tree))

        detail_panel = ttk.Frame(content, style="Panel.TFrame")
        detail_panel.grid(row=0, column=1, sticky="nsew")
        detail_panel.columnconfigure(0, weight=1)
        detail_panel.rowconfigure(0, weight=1)
        detail_canvas = tk.Canvas(detail_panel, background="#ffffff", highlightthickness=0)
        detail_canvas.grid(row=0, column=0, sticky="nsew")
        detail_scroll = ttk.Scrollbar(detail_panel, orient="vertical", command=detail_canvas.yview)
        detail_scroll.grid(row=0, column=1, sticky="ns")
        detail_canvas.configure(yscrollcommand=detail_scroll.set)
        self.detail_frame = ttk.Frame(detail_canvas, style="Panel.TFrame", padding=14)
        detail_window = detail_canvas.create_window((0, 0), window=self.detail_frame, anchor="nw")
        self.detail_frame.bind("<Configure>", lambda _event: detail_canvas.configure(scrollregion=detail_canvas.bbox("all")))
        detail_canvas.bind("<Configure>", lambda event: detail_canvas.itemconfigure(detail_window, width=event.width))
        detail_canvas.bind("<MouseWheel>", lambda event: detail_canvas.yview_scroll(int(-event.delta / 60), "units"))

        self.detail_frame.columnconfigure(0, weight=1)
        self.tr_label(self.detail_frame, "Selected Issue", style="Subheader.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 10))
        self.detail_vars = {
            "Title": tk.StringVar(value="-"),
            "Status": tk.StringVar(value="-"),
            "Line / Instrument": tk.StringVar(value="-"),
            "Category": tk.StringVar(value="-"),
            "Issue Time": tk.StringVar(value="-"),
            "Logged By": tk.StringVar(value="-"),
            "Downtime Duration": tk.StringVar(value="-"),
        }
        for row_index, (label, variable) in enumerate(self.detail_vars.items(), start=1):
            self.tr_label(self.detail_frame, label, style="Panel.TLabel").grid(row=row_index * 2 - 1, column=0, sticky="w", pady=(7, 0))
            ttk.Label(self.detail_frame, textvariable=variable, style="Panel.TLabel", wraplength=260).grid(row=row_index * 2, column=0, sticky="w")

        self.tr_label(self.detail_frame, "Description", style="Panel.TLabel").grid(row=16, column=0, sticky="w", pady=(14, 0))
        self.detail_description = tk.Text(self.detail_frame, height=12, wrap="word", font=("Segoe UI", 9), state="disabled")
        self.detail_description.grid(row=17, column=0, sticky="nsew", pady=(3, 0))
        self.detail_description.bind("<MouseWheel>", lambda event: detail_canvas.yview_scroll(int(-event.delta / 60), "units"))

    def add_dashboard_card(self, parent: ttk.Frame, title: str) -> None:
        card = ttk.Frame(parent, style="Card.TFrame", padding=12)
        card.pack(side="left", fill="x", expand=True, padx=(0, 10))
        value = tk.StringVar(value="0")
        self.dashboard_vars[title] = value
        self.tr_label(card, title, style="CardTitle.TLabel").pack(anchor="w")
        ttk.Label(card, textvariable=value, style="CardValue.TLabel").pack(anchor="w", pady=(4, 0))

    def build_entry_tab(self) -> None:
        panel = ttk.Frame(self.entry_tab, style="Panel.TFrame", padding=18)
        panel.pack(fill="both", expand=True)
        panel.columnconfigure(1, weight=1)
        panel.columnconfigure(3, weight=1)
        panel.rowconfigure(8, weight=1)

        self.tr_label(panel, "Issue Record", style="Subheader.TLabel").grid(row=0, column=0, columnspan=4, sticky="w", pady=(0, 12))

        self.issue_date_var = tk.StringVar()
        self.issue_hour_var = tk.StringVar()
        self.issue_minute_var = tk.StringVar()
        self.resolved_time_var = tk.StringVar(value="00:00")
        self.line_var = tk.StringVar(value=LINES[0])
        self.instrument_var = tk.StringVar(value=INSTRUMENTS[0])
        self.selected_instruments = {INSTRUMENTS[0]}
        self.category_var = tk.StringVar(value=CATEGORIES[0])
        self.subcategory_var = tk.StringVar(value=CATEGORY_MAP[CATEGORIES[0]][0])
        self.status_var = tk.StringVar(value=STATUS_OPTIONS[0])
        self.title_var = tk.StringVar()
        self.set_issue_datetime(now_text())

        self.add_line_instrument_grid(panel, 1)
        self.add_datetime_picker(panel, "Issue Time", 2, 0)
        category_combo = self.add_labeled_combo(panel, "Category", self.category_var, CATEGORIES, 2, 2)
        category_combo.bind("<<ComboboxSelected>>", lambda _event: self.update_subcategories())
        self.subcategory_combo = self.add_labeled_combo(panel, "Subcategory", self.subcategory_var, CATEGORY_MAP[self.category_var.get()], 3, 0)
        self.add_labeled_combo(panel, "Status", self.status_var, STATUS_OPTIONS, 3, 2)
        self.add_labeled_entry(panel, "Downtime Duration", self.resolved_time_var, 4, 0)
        self.add_labeled_entry(panel, "Title", self.title_var, 5, 0, columnspan=3)

        self.tr_label(panel, "Description", style="Panel.TLabel").grid(row=6, column=0, sticky="nw", pady=7)
        self.description_text = tk.Text(panel, height=7, wrap="word", font=("Segoe UI", 10))
        self.description_text.grid(row=6, column=1, columnspan=3, sticky="nsew", pady=7)

        self.tr_label(panel, "Resolution Notes", style="Panel.TLabel").grid(row=7, column=0, sticky="nw", pady=7)
        self.resolution_text = tk.Text(panel, height=5, wrap="word", font=("Segoe UI", 10))
        self.resolution_text.grid(row=7, column=1, columnspan=3, sticky="nsew", pady=7)

        actions = ttk.Frame(panel, style="Panel.TFrame")
        actions.grid(row=9, column=0, columnspan=4, sticky="ew", pady=(14, 0))
        self.tr_button(actions, "New", self.clear_form, prefix="+ ").pack(side="left")
        self.tr_button(actions, "Delete", self.delete_loaded_issue, prefix="✕ ").pack(side="left", padx=8)
        self.tr_button(actions, "Save", self.save_issue, prefix="✓ ", style="Accent.TButton").pack(side="right")

    def add_line_instrument_grid(self, parent: ttk.Frame, row: int) -> None:
        grid = ttk.Frame(parent, style="Panel.TFrame")
        grid.grid(row=row, column=0, columnspan=4, sticky="ew", pady=(0, 12))
        self.line_buttons: dict[str, tk.Button] = {}
        self.instrument_buttons: dict[str, tk.Button] = {}
        self.tr_label(grid, "Line", style="Panel.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 8), pady=3)
        for column_index, line in enumerate(LINES, start=1):
            button = tk.Button(
                grid,
                text=line,
                width=10,
                relief="raised",
                command=lambda selected_line=line: self.select_line(selected_line),
            )
            button.grid(
                row=0, column=column_index, padx=2, pady=3
            )
            self.line_buttons[line] = button
        self.tr_label(grid, "Vision", style="Panel.TLabel").grid(row=1, column=0, sticky="w", padx=(0, 8), pady=3)
        for column_index, instrument in enumerate(INSTRUMENTS, start=1):
            button = tk.Button(
                grid,
                text=instrument,
                width=14,
                relief="raised",
                command=lambda selected_instrument=instrument: self.select_instrument(selected_instrument),
            )
            button.grid(row=1, column=column_index, padx=2, pady=3)
            self.instrument_buttons[instrument] = button
        self.line_var.trace_add("write", lambda *_args: self.refresh_line_instrument_buttons())
        self.instrument_var.trace_add("write", lambda *_args: self.refresh_line_instrument_buttons())
        self.refresh_line_instrument_buttons()

    def select_line(self, line: str) -> None:
        self.line_var.set(line)

    def select_instrument(self, instrument: str) -> None:
        if instrument in self.selected_instruments and len(self.selected_instruments) > 1:
            self.selected_instruments.remove(instrument)
        else:
            self.selected_instruments.add(instrument)
        self.instrument_var.set(format_instruments(self.selected_instruments))

    def refresh_line_instrument_buttons(self) -> None:
        selected_bg = "#1f6feb"
        selected_fg = "#ffffff"
        default_bg = self.cget("bg")
        default_fg = "#111827"
        for line, button in getattr(self, "line_buttons", {}).items():
            is_selected = line == self.line_var.get()
            button.configure(
                background=selected_bg if is_selected else default_bg,
                foreground=selected_fg if is_selected else default_fg,
                relief="sunken" if is_selected else "raised",
            )
        current_instruments = set(split_instruments(self.instrument_var.get()))
        self.selected_instruments = current_instruments or {INSTRUMENTS[0]}
        if not current_instruments:
            self.instrument_var.set(format_instruments(self.selected_instruments))
            return
        for instrument, button in getattr(self, "instrument_buttons", {}).items():
            is_selected = instrument in current_instruments
            button.configure(
                background=selected_bg if is_selected else default_bg,
                foreground=selected_fg if is_selected else default_fg,
                relief="sunken" if is_selected else "raised",
            )

    def add_filter_instrument_buttons(self, parent: ttk.Frame, row: int) -> None:
        frame = ttk.Frame(parent, style="Panel.TFrame")
        frame.grid(row=row, column=0, columnspan=6, sticky="w", pady=(8, 0))
        self.tr_label(frame, "Vision Filter", style="Panel.TLabel").pack(side="left", padx=(4, 8))
        self.filter_instrument_buttons: dict[str, tk.Button] = {}
        for instrument in INSTRUMENTS:
            button = tk.Button(
                frame,
                text=instrument,
                width=12,
                relief="raised",
                command=lambda selected_instrument=instrument: self.toggle_filter_instrument(selected_instrument),
            )
            button.pack(side="left", padx=(0, 4))
            self.filter_instrument_buttons[instrument] = button

    def toggle_filter_instrument(self, instrument: str) -> None:
        if instrument in self.filter_instruments:
            self.filter_instruments.remove(instrument)
        else:
            self.filter_instruments.add(instrument)
        self.filter_instrument.set(format_instruments(self.filter_instruments))
        self.refresh_filter_instrument_buttons()
        self.search_records()

    def refresh_filter_instrument_buttons(self) -> None:
        selected_bg = "#1f6feb"
        selected_fg = "#ffffff"
        default_bg = self.cget("bg")
        default_fg = "#111827"
        for instrument, button in getattr(self, "filter_instrument_buttons", {}).items():
            is_selected = instrument in self.filter_instruments
            button.configure(
                background=selected_bg if is_selected else default_bg,
                foreground=selected_fg if is_selected else default_fg,
                relief="sunken" if is_selected else "raised",
            )

    def build_search_tab(self) -> None:
        filters = ttk.Frame(self.search_tab, style="Panel.TFrame", padding=14)
        filters.pack(fill="x", pady=(0, 10))

        self.filter_status = tk.StringVar()
        self.filter_line = tk.StringVar()
        self.filter_instrument = tk.StringVar()
        self.filter_instruments: set[str] = set()
        self.filter_category = tk.StringVar()
        self.filter_subcategory = tk.StringVar()
        self.filter_keyword = tk.StringVar()
        self.filter_from = tk.StringVar()
        self.filter_to = tk.StringVar()
        self.reset_search_date_bounds()

        self.add_filter_combo(filters, "Status", self.filter_status, [""] + STATUS_OPTIONS, 0, 0)
        self.add_filter_combo(filters, "Line", self.filter_line, [""] + LINES, 0, 2)
        category_filter = self.add_filter_combo(filters, "Category", self.filter_category, [""] + CATEGORIES, 1, 0)
        category_filter.bind("<<ComboboxSelected>>", lambda _event: self.update_filter_subcategories())
        self.filter_subcategory_combo = self.add_filter_combo(filters, "Subcategory", self.filter_subcategory, [""], 1, 2)
        self.add_filter_entry(filters, "Keyword", self.filter_keyword, 1, 4)
        self.add_filter_entry(filters, "From", self.filter_from, 2, 0)
        self.add_filter_entry(filters, "To", self.filter_to, 2, 2)

        self.add_filter_instrument_buttons(filters, 3)

        quick_filters = ttk.Frame(filters, style="Panel.TFrame")
        quick_filters.grid(row=4, column=0, columnspan=6, sticky="w", pady=(8, 0))
        self.tr_button(quick_filters, "Today", lambda: self.apply_quick_filter("today")).pack(side="left", padx=(0, 6))
        self.tr_button(quick_filters, "This Week", lambda: self.apply_quick_filter("week")).pack(side="left", padx=(0, 6))
        self.tr_button(quick_filters, "Action Required", lambda: self.apply_quick_filter("action")).pack(side="left", padx=(0, 6))
        self.tr_button(quick_filters, "Monitoring", lambda: self.apply_quick_filter("monitoring")).pack(side="left", padx=(0, 6))
        self.tr_button(quick_filters, "Camera Grab Fail", lambda: self.apply_quick_filter("camera_grab")).pack(side="left", padx=(0, 6))
        self.tr_button(quick_filters, "Recipe Issues", lambda: self.apply_quick_filter("recipe")).pack(side="left", padx=(0, 6))
        self.tr_button(quick_filters, "Clear", self.clear_search_filters).pack(side="left")

        buttons = ttk.Frame(filters, style="Panel.TFrame")
        buttons.grid(row=2, column=4, columnspan=2, sticky="e", padx=6, pady=6)
        self.tr_button(buttons, "Search", self.search_records, prefix="⌕ ", style="Accent.TButton").pack(side="left", padx=(0, 8))
        self.tr_button(buttons, "Excel", self.export_search_results, prefix="⇩ ").pack(side="left")
        self.tr_button(buttons, "Delete", lambda: self.delete_selected_issue(self.search_tree), prefix="✕ ").pack(side="left", padx=(8, 0))

        search_table = ttk.Frame(self.search_tab)
        search_table.pack(fill="both", expand=True)
        search_table.columnconfigure(0, weight=1)
        search_table.rowconfigure(0, weight=1)
        self.search_tree = self.make_issue_tree(search_table)
        self.search_tree.grid(row=0, column=0, sticky="nsew")
        search_scroll = ttk.Scrollbar(search_table, orient="vertical", command=self.search_tree.yview)
        search_scroll.grid(row=0, column=1, sticky="ns")
        self.search_tree.configure(yscrollcommand=search_scroll.set)
        self.search_tree.bind("<Double-1>", lambda _event: self.load_selected_search_issue())

    def build_version_tab(self) -> None:
        content = ttk.Frame(self.version_tab)
        content.pack(fill="both", expand=True)
        content.columnconfigure(0, weight=5)
        content.columnconfigure(1, weight=1)
        content.rowconfigure(1, weight=1)

        dashboard_panel = ttk.Frame(content, style="Panel.TFrame", padding=12)
        dashboard_panel.grid(row=0, column=0, columnspan=2, sticky="ew", pady=(0, 10))
        self.tr_label(dashboard_panel, "Version Dashboard", style="Subheader.TLabel").pack(anchor="w", pady=(0, 8))
        self.version_dashboard = ttk.Frame(dashboard_panel, style="Panel.TFrame")
        self.version_dashboard.pack(fill="x")
        self.version_cards: dict[tuple[str, str], tk.Label] = {}
        for column_index, instrument in enumerate(INSTRUMENTS):
            section = ttk.Frame(self.version_dashboard, style="Panel.TFrame", padding=6)
            section.grid(row=0, column=column_index, sticky="nsew", padx=(0, 6))
            ttk.Label(section, text=instrument, style="Subheader.TLabel", anchor="center").pack(fill="x")
            for line in LINES:
                card = tk.Label(
                    section,
                    text=f"{line}\nNo Version",
                    justify="left",
                    anchor="nw",
                    width=17,
                    height=5,
                    bg="#ffffff",
                    fg="#111827",
                    relief="solid",
                    bd=1,
                    padx=6,
                    pady=5,
                    font=("Segoe UI", 8),
                )
                card.pack(fill="x", pady=(6, 0))
                card.bind("<Button-1>", lambda _event, selected_line=line, selected_instrument=instrument: self.select_version_target(selected_line, selected_instrument))
                self.version_cards[(line, instrument)] = card
            self.version_dashboard.columnconfigure(column_index, weight=1)

        editor_panel = ttk.Frame(content, style="Panel.TFrame")
        editor_panel.grid(row=1, column=0, sticky="nsew", padx=(0, 10))
        editor_panel.columnconfigure(0, weight=1)
        editor_panel.rowconfigure(0, weight=1)
        editor_canvas = tk.Canvas(editor_panel, background="#ffffff", highlightthickness=0)
        editor_canvas.grid(row=0, column=0, sticky="nsew")
        editor_scroll = ttk.Scrollbar(editor_panel, orient="vertical", command=editor_canvas.yview)
        editor_scroll.grid(row=0, column=1, sticky="ns")
        editor_canvas.configure(yscrollcommand=editor_scroll.set)
        editor = ttk.Frame(editor_canvas, style="Panel.TFrame", padding=14)
        editor_window = editor_canvas.create_window((0, 0), window=editor, anchor="nw")
        editor.bind("<Configure>", lambda _event: editor_canvas.configure(scrollregion=editor_canvas.bbox("all")))
        editor_canvas.bind("<Configure>", lambda event: editor_canvas.itemconfigure(editor_window, width=event.width))
        editor_canvas.bind("<MouseWheel>", lambda event: editor_canvas.yview_scroll(int(-event.delta / 60), "units"))
        editor.columnconfigure(1, weight=1)
        editor.columnconfigure(3, weight=1)
        editor.rowconfigure(5, weight=1)
        self.tr_label(editor, "Version Update", style="Subheader.TLabel").grid(row=0, column=0, columnspan=4, sticky="w", pady=(0, 10))

        self.version_group_var = tk.StringVar(value=list(VERSION_GROUPS.keys())[0])
        self.version_template_var = tk.StringVar()
        self.version_update_time_var = tk.StringVar(value=now_text())
        self.version_sw_var = tk.StringVar()
        self.version_algo_var = tk.StringVar()
        self.version_create_issue_var = tk.BooleanVar(value=True)
        self.version_selected_lines = {LINES[0]}
        self.version_selected_instruments = {VERSION_GROUPS[self.version_group_var.get()][0]}

        group_combo = self.add_labeled_combo(editor, "Version Group", self.version_group_var, list(VERSION_GROUPS.keys()), 1, 0)
        group_combo.bind("<<ComboboxSelected>>", lambda _event: self.on_version_group_changed())
        self.version_template_combo = self.add_labeled_combo(editor, "Version Template", self.version_template_var, [], 1, 2)
        self.version_template_combo.bind("<<ComboboxSelected>>", lambda _event: self.load_selected_version_template())
        self.add_labeled_entry(editor, "Update Time", self.version_update_time_var, 2, 0)
        self.add_labeled_entry(editor, "SW Version", self.version_sw_var, 2, 2)
        self.add_labeled_entry(editor, "Algo Version", self.version_algo_var, 3, 0)
        self.version_create_issue_button = tk.Button(
            editor,
            anchor="w",
            relief="raised",
            command=self.toggle_create_issue_option,
        )
        self.version_create_issue_button.grid(row=3, column=2, columnspan=2, sticky="w", pady=7)
        self.refresh_create_issue_button()

        target_frame = ttk.Frame(editor, style="Panel.TFrame")
        target_frame.grid(row=4, column=0, columnspan=4, sticky="ew", pady=(6, 8))
        self.version_line_buttons: dict[str, tk.Button] = {}
        self.version_instrument_buttons: dict[str, tk.Button] = {}
        self.tr_label(target_frame, "Line", style="Panel.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 8), pady=3)
        for column_index, line in enumerate(LINES, start=1):
            button = tk.Button(target_frame, text=line, width=10, command=lambda selected_line=line: self.toggle_version_line(selected_line))
            button.grid(row=0, column=column_index, padx=2, pady=3)
            self.version_line_buttons[line] = button
        self.tr_label(target_frame, "Vision", style="Panel.TLabel").grid(row=1, column=0, sticky="w", padx=(0, 8), pady=3)
        for column_index, instrument in enumerate(INSTRUMENTS, start=1):
            button = tk.Button(target_frame, text=instrument, width=14, command=lambda selected_instrument=instrument: self.toggle_version_instrument(selected_instrument))
            button.grid(row=1, column=column_index, padx=2, pady=3)
            self.version_instrument_buttons[instrument] = button

        self.tr_label(editor, "Description", style="Panel.TLabel").grid(row=5, column=0, sticky="nw", pady=7)
        self.version_description_text = tk.Text(editor, height=5, wrap="word", font=("Segoe UI", 10))
        self.version_description_text.grid(row=5, column=1, columnspan=3, sticky="nsew", pady=7)
        self.version_description_text.bind("<MouseWheel>", lambda event: editor_canvas.yview_scroll(int(-event.delta / 60), "units"))

        actions = ttk.Frame(editor, style="Panel.TFrame")
        actions.grid(row=6, column=0, columnspan=4, sticky="ew", pady=(10, 0))
        self.tr_button(actions, "Refresh", self.refresh_version_history, prefix="↻ ").pack(side="left")
        self.tr_button(actions, "Save Version Update", self.save_version_updates, prefix="✓ ", style="Accent.TButton").pack(side="right")

        description_panel = ttk.Frame(content, style="Panel.TFrame", padding=12)
        description_panel.grid(row=1, column=1, sticky="nsew")
        description_panel.columnconfigure(0, weight=1)
        description_panel.rowconfigure(2, weight=1)
        description_panel.rowconfigure(4, weight=2)
        self.tr_label(description_panel, "Version Description", style="Subheader.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 8))
        group_buttons = ttk.Frame(description_panel, style="Panel.TFrame")
        group_buttons.grid(row=1, column=0, sticky="ew", pady=(0, 8))
        self.version_description_group_var = tk.StringVar(value=list(VERSION_GROUPS.keys())[0])
        self.version_description_buttons: dict[str, tk.Button] = {}
        for index, group_name in enumerate(VERSION_GROUPS):
            button = tk.Button(
                group_buttons,
                text=group_name,
                width=10,
                command=lambda selected_group=group_name: self.select_description_group(selected_group),
            )
            button.grid(row=index // 2, column=index % 2, sticky="ew", padx=2, pady=2)
            self.version_description_buttons[group_name] = button
        group_buttons.columnconfigure(0, weight=1)
        group_buttons.columnconfigure(1, weight=1)

        self.version_description_list = tk.Listbox(description_panel, height=7, exportselection=False, font=("Segoe UI", 9))
        self.version_description_list.grid(row=2, column=0, sticky="nsew")
        self.version_description_list.bind("<<ListboxSelect>>", lambda _event: self.show_selected_version_description())
        self.tr_label(description_panel, "Description", style="Panel.TLabel").grid(row=3, column=0, sticky="w", pady=(8, 2))
        description_text_frame = ttk.Frame(description_panel, style="Panel.TFrame")
        description_text_frame.grid(row=4, column=0, sticky="nsew")
        description_text_frame.columnconfigure(0, weight=1)
        description_text_frame.rowconfigure(0, weight=1)
        self.version_description_view = tk.Text(description_text_frame, height=9, wrap="word", font=("Segoe UI", 9), state="disabled")
        self.version_description_view.grid(row=0, column=0, sticky="nsew")
        version_description_scroll = ttk.Scrollbar(description_text_frame, orient="vertical", command=self.version_description_view.yview)
        version_description_scroll.grid(row=0, column=1, sticky="ns")
        self.version_description_view.configure(yscrollcommand=version_description_scroll.set)

        self.on_version_group_changed()
        self.refresh_version_history()

    def toggle_create_issue_option(self) -> None:
        self.version_create_issue_var.set(not self.version_create_issue_var.get())
        self.refresh_create_issue_button()

    def refresh_create_issue_button(self) -> None:
        marker = "☑" if self.version_create_issue_var.get() else "☐"
        self.version_create_issue_button.configure(text=f"{marker} {self.text('Create Monitoring Issue')}")

    def on_version_group_changed(self) -> None:
        group_name = self.version_group_var.get()
        allowed = VERSION_GROUPS[group_name]
        self.version_selected_instruments = {allowed[0]}
        self.refresh_version_target_buttons()
        self.refresh_version_templates()

    def refresh_version_templates(self) -> None:
        templates = recent_version_templates(self.version_group_var.get(), 3)
        self.version_template_rows = {
            f"{row['sw_version']} / {row['algo_version']}": row
            for row in templates
        }
        values = list(self.version_template_rows.keys())
        self.version_template_combo.configure(values=values)
        if values:
            self.version_template_var.set(values[0])
            self.load_selected_version_template()
        else:
            self.version_template_var.set("")
            self.version_sw_var.set("")
            self.version_algo_var.set("")
            self.version_description_text.delete("1.0", "end")

    def select_description_group(self, group_name: str) -> None:
        self.version_description_group_var.set(group_name)
        self.populate_version_description_dashboard()

    def refresh_description_group_buttons(self) -> None:
        selected_bg = "#1f6feb"
        selected_fg = "#ffffff"
        default_bg = self.cget("bg")
        default_fg = "#111827"
        for group_name, button in self.version_description_buttons.items():
            is_selected = group_name == self.version_description_group_var.get()
            button.configure(
                background=selected_bg if is_selected else default_bg,
                foreground=selected_fg if is_selected else default_fg,
                relief="sunken" if is_selected else "raised",
            )

    def populate_version_description_dashboard(self) -> None:
        self.refresh_description_group_buttons()
        rows = recent_version_templates(self.version_description_group_var.get(), 3)
        self.version_description_rows = rows
        self.version_description_list.delete(0, "end")
        for row in rows:
            self.version_description_list.insert("end", f"SW {row['sw_version']} / Algo {row['algo_version']}")
        if rows:
            self.version_description_list.selection_set(0)
            self.show_selected_version_description()
        else:
            self.set_version_description_text("No version template saved.")

    def show_selected_version_description(self) -> None:
        selection = self.version_description_list.curselection()
        if not selection:
            self.set_version_description_text("")
            return
        row = self.version_description_rows[selection[0]]
        description = row["description"] or ""
        text = (
            f"Group: {row['group_name']}\n"
            f"SW Version: {row['sw_version']}\n"
            f"Algo Version: {row['algo_version']}\n"
            f"Updated: {row['updated_at']}\n"
            f"Worker: {row['worker']}\n\n"
            f"{description}"
        )
        self.set_version_description_text(text)

    def set_version_description_text(self, value: str) -> None:
        self.version_description_view.configure(state="normal")
        self.version_description_view.delete("1.0", "end")
        self.version_description_view.insert("1.0", value)
        self.version_description_view.configure(state="disabled")

    def load_selected_version_template(self) -> None:
        row = getattr(self, "version_template_rows", {}).get(self.version_template_var.get())
        if row is None:
            return
        self.version_sw_var.set(row["sw_version"])
        self.version_algo_var.set(row["algo_version"])
        self.version_description_text.delete("1.0", "end")
        self.version_description_text.insert("1.0", row["description"] or "")

    def select_version_target(self, line: str, instrument: str) -> None:
        self.version_group_var.set(INSTRUMENT_GROUP[instrument])
        self.version_selected_lines = {line}
        self.version_selected_instruments = {instrument}
        self.refresh_version_templates()
        self.refresh_version_target_buttons()

    def toggle_version_line(self, line: str) -> None:
        if line in self.version_selected_lines and len(self.version_selected_lines) > 1:
            self.version_selected_lines.remove(line)
        else:
            self.version_selected_lines.add(line)
        self.refresh_version_target_buttons()

    def toggle_version_instrument(self, instrument: str) -> None:
        if INSTRUMENT_GROUP[instrument] != self.version_group_var.get():
            return
        if instrument in self.version_selected_instruments and len(self.version_selected_instruments) > 1:
            self.version_selected_instruments.remove(instrument)
        else:
            self.version_selected_instruments.add(instrument)
        self.refresh_version_target_buttons()

    def refresh_version_target_buttons(self) -> None:
        selected_bg = "#1f6feb"
        selected_fg = "#ffffff"
        disabled_bg = "#e5e7eb"
        default_bg = self.cget("bg")
        default_fg = "#111827"
        for line, button in self.version_line_buttons.items():
            is_selected = line in self.version_selected_lines
            button.configure(
                background=selected_bg if is_selected else default_bg,
                foreground=selected_fg if is_selected else default_fg,
                relief="sunken" if is_selected else "raised",
            )
        group_name = self.version_group_var.get()
        for instrument, button in self.version_instrument_buttons.items():
            is_allowed = INSTRUMENT_GROUP[instrument] == group_name
            is_selected = instrument in self.version_selected_instruments
            button.configure(
                state="normal" if is_allowed else "disabled",
                background=selected_bg if is_selected else (default_bg if is_allowed else disabled_bg),
                foreground=selected_fg if is_selected else default_fg,
                relief="sunken" if is_selected else "raised",
            )

    def save_version_updates(self) -> None:
        lines = sorted(self.version_selected_lines, key=LINES.index)
        instruments = sorted(self.version_selected_instruments, key=INSTRUMENTS.index)
        if not lines or not instruments:
            messagebox.showwarning(APP_TITLE, "Select at least one line and one vision.")
            return
        description = self.version_description_text.get("1.0", "end").strip()
        saved_count = 0
        try:
            for line in lines:
                for instrument in instruments:
                    create_version_update(
                        VersionInput(
                            update_time=self.version_update_time_var.get().strip(),
                            group_name=self.version_group_var.get(),
                            line=line,
                            instrument=instrument,
                            sw_version=self.version_sw_var.get().strip(),
                            algo_version=self.version_algo_var.get().strip(),
                            description=description,
                            worker=self.current_worker_var.get().strip(),
                        ),
                        self.version_create_issue_var.get(),
                    )
                    saved_count += 1
        except ValueError as exc:
            messagebox.showerror(APP_TITLE, str(exc))
            return
        self.refresh_version_history()
        self.refresh_open_issues()
        self.search_records()
        messagebox.showinfo(APP_TITLE, f"{saved_count} version update record(s) saved.")

    def refresh_version_history(self) -> None:
        self.populate_version_dashboard()
        self.refresh_version_templates()
        self.populate_version_description_dashboard()

    def populate_version_dashboard(self) -> None:
        latest = latest_version_by_instrument()
        recent_threshold = datetime.now() - timedelta(days=7)
        for line in LINES:
            for instrument in INSTRUMENTS:
                row = latest.get((line, instrument))
                card = self.version_cards[(line, instrument)]
                if row is None:
                    card.configure(text=f"{line}\nNo Version", bg="#fff1f2", fg="#9f1239")
                    continue
                text = (
                    f"{line}\n"
                    f"SW {row['sw_version']}\n"
                    f"Algo {row['algo_version']}\n"
                    f"{row['update_time']}"
                )
                try:
                    updated = datetime.strptime(row["update_time"], "%Y-%m-%d %H:%M")
                except ValueError:
                    updated = datetime.min
                bg = "#ecfdf3" if updated >= recent_threshold else "#ffffff"
                fg = "#166534" if updated >= recent_threshold else "#111827"
                card.configure(text=text, bg=bg, fg=fg)

    def make_issue_tree(self, parent: ttk.Frame) -> ttk.Treeview:
        columns = ("id", "issue_time", "line", "instrument", "category", "subcategory", "title", "status", "worker")
        tree = ttk.Treeview(parent, columns=columns, show="headings", selectmode="browse")
        headings = {
            "id": "ID",
            "issue_time": "Issue Time",
            "line": "Line",
            "instrument": "Instrument",
            "category": "Category",
            "subcategory": "Subcategory",
            "title": "Title",
            "status": "Status",
            "worker": "Logged By",
        }
        widths = {
            "id": 58,
            "issue_time": 140,
            "line": 72,
            "instrument": 190,
            "category": 110,
            "subcategory": 170,
            "title": 290,
            "status": 100,
            "worker": 130,
        }
        for column in columns:
            tree.heading(column, text=self.text(headings[column]))
            tree.column(column, width=widths[column], anchor="w")
        for status, (background, foreground) in STATUS_TAGS.items():
            tree.tag_configure(status, background=background, foreground=foreground)
        tree.bind("<MouseWheel>", lambda event: tree.yview_scroll(int(-event.delta / 60), "units"))
        return tree

    def add_labeled_entry(self, parent: ttk.Frame, label: str, variable: tk.StringVar, row: int, column: int, columnspan: int = 1) -> ttk.Entry:
        self.tr_label(parent, label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(0, 8), pady=7)
        entry = ttk.Entry(parent, textvariable=variable)
        entry.grid(row=row, column=column + 1, columnspan=columnspan, sticky="ew", pady=7)
        return entry

    def add_datetime_picker(self, parent: ttk.Frame, label: str, row: int, column: int) -> None:
        self.tr_label(parent, label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(0, 8), pady=7)
        frame = ttk.Frame(parent, style="Panel.TFrame")
        frame.grid(row=row, column=column + 1, sticky="w", pady=7)
        self.issue_date_entry = ttk.Entry(frame, textvariable=self.issue_date_var, width=12)
        self.issue_date_entry.pack(side="left", padx=(0, 10))
        self.issue_date_entry.bind("<Button-1>", self.open_calendar_popup)
        tk.Spinbox(frame, from_=0, to=23, textvariable=self.issue_hour_var, width=3, wrap=True, format="%02.0f").pack(side="left")
        ttk.Label(frame, text=":", style="Panel.TLabel").pack(side="left", padx=3)
        tk.Spinbox(frame, from_=0, to=59, textvariable=self.issue_minute_var, width=3, wrap=True, format="%02.0f").pack(side="left")

    def open_calendar_popup(self, _event: tk.Event | None = None) -> None:
        if hasattr(self, "calendar_popup") and self.calendar_popup.winfo_exists():
            self.position_calendar_popup(self.calendar_popup)
            return
        popup = tk.Toplevel(self)
        self.calendar_popup = popup
        popup.title("Select Issue Date")
        popup.resizable(False, False)
        popup.transient(self)
        popup.overrideredirect(True)
        popup.bind("<FocusOut>", lambda _event: popup.destroy())

        selected = self.parse_issue_datetime()
        year_var = tk.IntVar(value=selected.year)
        month_var = tk.IntVar(value=selected.month)

        header = ttk.Frame(popup, padding=8)
        header.pack(fill="x")

        body = ttk.Frame(popup, padding=(8, 0, 8, 8))
        body.pack()

        def draw_calendar() -> None:
            for child in body.winfo_children():
                child.destroy()
            for index, day_name in enumerate(["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"]):
                ttk.Label(body, text=day_name, width=5, anchor="center").grid(row=0, column=index, padx=1, pady=1)
            month_days = calendar.monthcalendar(year_var.get(), month_var.get())
            for week_index, week in enumerate(month_days, start=1):
                for day_index, day in enumerate(week):
                    if day == 0:
                        ttk.Label(body, text="", width=5).grid(row=week_index, column=day_index, padx=1, pady=1)
                        continue
                    date_text = f"{year_var.get():04d}-{month_var.get():02d}-{day:02d}"
                    ttk.Button(body, text=str(day), width=4, command=lambda value=date_text: select_date(value)).grid(
                        row=week_index, column=day_index, padx=1, pady=1
                    )

        def change_month(delta: int) -> None:
            month = month_var.get() + delta
            year = year_var.get()
            if month < 1:
                month = 12
                year -= 1
            elif month > 12:
                month = 1
                year += 1
            month_var.set(month)
            year_var.set(year)
            title_var.set(f"{calendar.month_name[month]} {year}")
            draw_calendar()

        def select_date(date_text: str) -> None:
            self.issue_date_var.set(date_text)
            popup.destroy()

        title_var = tk.StringVar(value=f"{calendar.month_name[month_var.get()]} {year_var.get()}")
        ttk.Button(header, text="<", width=3, command=lambda: change_month(-1)).pack(side="left")
        ttk.Label(header, textvariable=title_var, width=18, anchor="center").pack(side="left", padx=6)
        ttk.Button(header, text=">", width=3, command=lambda: change_month(1)).pack(side="left")
        draw_calendar()
        popup.update_idletasks()
        self.position_calendar_popup(popup)
        popup.lift()
        popup.focus_force()

    def position_calendar_popup(self, popup: tk.Toplevel) -> None:
        self.issue_date_entry.update_idletasks()
        x = self.issue_date_entry.winfo_rootx()
        y = self.issue_date_entry.winfo_rooty() + self.issue_date_entry.winfo_height()
        popup_width = max(popup.winfo_reqwidth(), 220)
        popup_height = max(popup.winfo_reqheight(), 180)
        screen_width = self.winfo_screenwidth()
        screen_height = self.winfo_screenheight()
        x = max(0, min(x, screen_width - popup_width - 8))
        if y + popup_height > screen_height:
            y = max(0, self.issue_date_entry.winfo_rooty() - popup_height)
        popup.geometry(f"+{x}+{y}")

    def add_labeled_combo(self, parent: ttk.Frame, label: str, variable: tk.StringVar, values: list[str], row: int, column: int) -> ttk.Combobox:
        self.tr_label(parent, label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(0, 8), pady=7)
        combo = ttk.Combobox(parent, textvariable=variable, values=values, state="readonly")
        combo.grid(row=row, column=column + 1, sticky="ew", pady=7)
        return combo

    def add_filter_combo(self, parent: ttk.Frame, label: str, variable: tk.StringVar, values: list[str], row: int, column: int) -> ttk.Combobox:
        self.tr_label(parent, label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(4, 8), pady=6)
        combo = ttk.Combobox(parent, textvariable=variable, values=values, state="readonly", width=20)
        combo.grid(row=row, column=column + 1, sticky="ew", padx=(0, 12), pady=6)
        return combo

    def add_filter_entry(self, parent: ttk.Frame, label: str, variable: tk.StringVar, row: int, column: int) -> ttk.Entry:
        self.tr_label(parent, label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(4, 8), pady=6)
        entry = ttk.Entry(parent, textvariable=variable, width=22)
        entry.grid(row=row, column=column + 1, sticky="ew", padx=(0, 12), pady=6)
        return entry

    def update_subcategories(self) -> None:
        values = CATEGORY_MAP.get(self.category_var.get(), [""])
        self.subcategory_combo.configure(values=values)
        self.subcategory_var.set(values[0])

    def update_filter_subcategories(self) -> None:
        category = self.filter_category.get()
        values = [""] + CATEGORY_MAP.get(category, [])
        self.filter_subcategory_combo.configure(values=values)
        self.filter_subcategory.set("")

    def form_issue(self) -> IssueInput:
        worker = self.loaded_issue_worker or self.current_worker_var.get().strip()
        return IssueInput(
            issue_time=self.issue_datetime_text(),
            resolved_time=self.resolved_time_var.get().strip() or "00:00",
            line=self.line_var.get().strip(),
            instrument=self.instrument_var.get().strip(),
            worker=worker,
            category=self.category_var.get().strip(),
            subcategory=self.subcategory_var.get().strip(),
            title=self.title_var.get().strip(),
            description=self.description_text.get("1.0", "end").strip(),
            status=self.status_var.get().strip(),
            resolution_notes=self.resolution_text.get("1.0", "end").strip(),
        )

    def save_issue(self) -> None:
        try:
            issue = self.form_issue()
            if self.selected_issue_id is None:
                saved_id = create_issue(issue)
                messagebox.showinfo(APP_TITLE, "Issue saved.")
            else:
                update_issue(self.selected_issue_id, issue)
                saved_id = self.selected_issue_id
                messagebox.showinfo(APP_TITLE, "Issue updated.")
            self.refresh_open_issues()
            self.search_records()
            self.load_issue_into_form(saved_id)
            self.select_issue_in_tree(self.open_tree, saved_id)
            self.select_issue_in_tree(self.search_tree, saved_id)
        except ValueError as exc:
            messagebox.showerror(APP_TITLE, str(exc))

    def clear_form(self) -> None:
        self.selected_issue_id = None
        self.loaded_issue_worker = ""
        self.set_issue_datetime(now_text())
        self.resolved_time_var.set("00:00")
        self.line_var.set(LINES[0])
        self.selected_instruments = {INSTRUMENTS[0]}
        self.instrument_var.set(INSTRUMENTS[0])
        self.category_var.set(CATEGORIES[0])
        self.update_subcategories()
        self.status_var.set(ACTIVE_STATUS_OPTIONS[0])
        self.title_var.set("")
        self.description_text.delete("1.0", "end")
        self.resolution_text.delete("1.0", "end")

    def refresh_open_issues(self) -> None:
        rows = active_issues()
        self.populate_tree(self.open_tree, rows)
        self.refresh_dashboard()
        self.update_detail_panel(self.open_tree)

    def refresh_dashboard(self) -> None:
        counts = dashboard_counts()
        for title, variable in self.dashboard_vars.items():
            variable.set(str(counts.get(title, 0)))

    def search_records(self) -> None:
        filters = {
            "status": self.filter_status.get(),
            "line": self.filter_line.get(),
            "instrument": self.filter_instrument.get(),
            "category": self.filter_category.get(),
            "subcategory": self.filter_subcategory.get(),
            "keyword": self.filter_keyword.get(),
            "date_from": self.filter_from.get(),
            "date_to": self.filter_to.get(),
        }
        self.search_rows = search_issues(filters)
        self.populate_tree(self.search_tree, self.search_rows)

    def reset_search_date_bounds(self) -> None:
        first_time, latest_time = issue_time_bounds()
        self.filter_from.set(first_time)
        self.filter_to.set(latest_time)

    def apply_quick_filter(self, filter_name: str) -> None:
        if filter_name == "today":
            today = datetime.now().strftime("%Y-%m-%d")
            self.filter_from.set(f"{today} 00:00")
            self.filter_to.set(f"{today} 23:59")
        elif filter_name == "week":
            today_dt = datetime.now()
            start = today_dt - timedelta(days=today_dt.weekday())
            self.filter_from.set(start.strftime("%Y-%m-%d 00:00"))
            self.filter_to.set(today_dt.strftime("%Y-%m-%d 23:59"))
        elif filter_name == "action":
            self.filter_status.set("Action Required")
        elif filter_name == "monitoring":
            self.filter_status.set("Monitoring")
        elif filter_name == "camera_grab":
            self.filter_category.set("Camera Grab Fail")
            self.update_filter_subcategories()
        elif filter_name == "recipe":
            self.filter_category.set("Recipe")
            self.update_filter_subcategories()
        self.search_records()

    def clear_search_filters(self) -> None:
        for variable in [
            self.filter_status,
            self.filter_line,
            self.filter_category,
            self.filter_subcategory,
            self.filter_keyword,
        ]:
            variable.set("")
        self.filter_instruments.clear()
        self.filter_instrument.set("")
        self.refresh_filter_instrument_buttons()
        self.reset_search_date_bounds()
        self.update_filter_subcategories()
        self.search_records()

    def populate_tree(self, tree: ttk.Treeview, rows: list) -> None:
        for item in tree.get_children():
            tree.delete(item)
        for row_number, row in enumerate(rows, start=1):
            tree.insert(
                "",
                "end",
                iid=str(row["id"]),
                values=(
                    row_number,
                    row["issue_time"],
                    row["line"],
                    row["instrument"],
                    row["category"],
                    row["subcategory"],
                    row["title"],
                    row["status"],
                    row["worker"],
                ),
                tags=(row["status"],),
            )

    def selected_tree_id(self, tree: ttk.Treeview) -> int | None:
        selection = tree.selection()
        if not selection:
            return None
        return int(selection[0])

    def load_selected_open_issue(self) -> None:
        issue_id = self.selected_tree_id(self.open_tree)
        if issue_id is None:
            messagebox.showwarning(APP_TITLE, "Select an open issue first.")
            return
        self.load_issue_into_form(issue_id)

    def update_detail_panel(self, tree: ttk.Treeview) -> None:
        if not hasattr(self, "detail_vars"):
            return
        issue_id = self.selected_tree_id(tree)
        row = get_issue(issue_id) if issue_id is not None else None
        if row is None:
            for variable in self.detail_vars.values():
                variable.set("-")
            self.set_detail_description("")
            return
        category_text = row["category"]
        if row["subcategory"]:
            category_text = f"{category_text} / {row['subcategory']}"
        self.detail_vars["Title"].set(row["title"] or "-")
        self.detail_vars["Status"].set(row["status"] or "-")
        self.detail_vars["Line / Instrument"].set(f"{row['line']} / {row['instrument']}")
        self.detail_vars["Category"].set(category_text)
        self.detail_vars["Issue Time"].set(row["issue_time"] or "-")
        self.detail_vars["Logged By"].set(row["worker"] or "-")
        self.detail_vars["Downtime Duration"].set(row["resolved_time"] or "-")
        self.set_detail_description(row["description"] or "")

    def set_detail_description(self, value: str) -> None:
        self.detail_description.configure(state="normal")
        self.detail_description.delete("1.0", "end")
        self.detail_description.insert("1.0", value)
        self.detail_description.configure(state="disabled")

    def load_issue_into_form(self, issue_id: int) -> None:
        row = get_issue(issue_id)
        if row is None:
            messagebox.showerror(APP_TITLE, "Issue was not found.")
            return
        self.selected_issue_id = issue_id
        self.loaded_issue_worker = row["worker"] or ""
        self.set_issue_datetime(row["issue_time"])
        self.resolved_time_var.set(row["resolved_time"] or "")
        self.line_var.set(row["line"])
        self.instrument_var.set(row["instrument"])
        self.category_var.set(row["category"])
        self.update_subcategories()
        self.subcategory_var.set(row["subcategory"] or "")
        self.status_var.set(row["status"])
        self.title_var.set(row["title"])
        self.description_text.delete("1.0", "end")
        self.description_text.insert("1.0", row["description"] or "")
        self.resolution_text.delete("1.0", "end")
        self.resolution_text.insert("1.0", row["resolution_notes"] or "")
        self.notebook.select(self.entry_tab)

    def load_selected_search_issue(self) -> None:
        issue_id = self.selected_tree_id(self.search_tree)
        if issue_id is None:
            messagebox.showwarning(APP_TITLE, "Select a search result first.")
            return
        self.load_issue_into_form(issue_id)

    def select_issue_in_tree(self, tree: ttk.Treeview, issue_id: int) -> None:
        for item in tree.get_children():
            if int(item) == issue_id:
                tree.selection_set(item)
                tree.focus(item)
                tree.see(item)
                return

    def parse_issue_datetime(self) -> datetime:
        try:
            return datetime.strptime(self.issue_datetime_text(), "%Y-%m-%d %H:%M")
        except ValueError:
            return datetime.now()

    def set_issue_datetime(self, value: str) -> None:
        try:
            parsed = datetime.strptime(value, "%Y-%m-%d %H:%M")
        except ValueError:
            parsed = datetime.now()
        self.issue_date_var.set(parsed.strftime("%Y-%m-%d"))
        self.issue_hour_var.set(parsed.strftime("%H"))
        self.issue_minute_var.set(parsed.strftime("%M"))

    def issue_datetime_text(self) -> str:
        try:
            hour = max(0, min(23, int(self.issue_hour_var.get() or 0)))
        except ValueError:
            hour = 0
        try:
            minute = max(0, min(59, int(self.issue_minute_var.get() or 0)))
        except ValueError:
            minute = 0
        self.issue_hour_var.set(f"{hour:02d}")
        self.issue_minute_var.set(f"{minute:02d}")
        return f"{self.issue_date_var.get().strip()} {hour:02d}:{minute:02d}"

    def resolve_selected_open_issue(self) -> None:
        issue_id = self.selected_tree_id(self.open_tree)
        if issue_id is None:
            messagebox.showwarning(APP_TITLE, "Select an open issue first.")
            return
        resolve_issue(issue_id)
        self.refresh_open_issues()
        self.search_records()

    def quick_status_selected(self, tree: ttk.Treeview, status: str) -> None:
        issue_id = self.selected_tree_id(tree)
        if issue_id is None:
            messagebox.showwarning(APP_TITLE, "Select an issue first.")
            return
        try:
            set_issue_status(issue_id, status)
        except ValueError as exc:
            messagebox.showerror(APP_TITLE, str(exc))
            return
        self.refresh_open_issues()
        self.search_records()
        self.select_issue_in_tree(self.open_tree, issue_id)
        self.update_detail_panel(self.open_tree)

    def delete_loaded_issue(self) -> None:
        if self.selected_issue_id is None:
            messagebox.showwarning(APP_TITLE, "Load or select an issue first.")
            return
        self.delete_issue_by_id(self.selected_issue_id)

    def delete_selected_issue(self, tree: ttk.Treeview) -> None:
        issue_id = self.selected_tree_id(tree)
        if issue_id is None:
            messagebox.showwarning(APP_TITLE, "Select an issue first.")
            return
        self.delete_issue_by_id(issue_id)

    def delete_issue_by_id(self, issue_id: int) -> None:
        row = get_issue(issue_id)
        title = row["title"] if row else "selected issue"
        if not messagebox.askyesno(
            APP_TITLE,
            f"정말 삭제하시겠습니까?\n\n{title}\n\n삭제 후 되돌릴 수 없습니다.",
        ):
            return
        delete_issue(issue_id)
        if self.selected_issue_id == issue_id:
            self.clear_form()
        self.refresh_open_issues()
        self.search_records()

    def export_search_results(self) -> None:
        if not self.search_rows:
            messagebox.showwarning(APP_TITLE, "No search results to export.")
            return
        default_name = f"vision_issue_report_{now_text().replace(':', '').replace(' ', '_')}.xlsx"
        output = filedialog.asksaveasfilename(
            title="Save Excel Report",
            defaultextension=".xlsx",
            initialfile=default_name,
            filetypes=[("Excel Workbook", "*.xlsx")],
        )
        if not output:
            return
        export_issues_to_excel(self.search_rows, Path(output))
        messagebox.showinfo(APP_TITLE, f"Excel report saved:\n{output}")


if __name__ == "__main__":
    app = VisionIssueApp()
    app.mainloop()
