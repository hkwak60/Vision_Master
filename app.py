from __future__ import annotations

import calendar
import tkinter as tk
from datetime import datetime, timedelta
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from vision_tracker import (
    ACTIVE_STATUS_OPTIONS,
    APP_TITLE,
    CATEGORIES,
    CATEGORY_MAP,
    INSTRUMENTS,
    LINES,
    STATUS_OPTIONS,
    WORKERS,
    IssueInput,
    active_issues,
    create_issue,
    dashboard_counts,
    delete_issue,
    export_issues_to_excel,
    get_issue,
    initialize_database,
    now_text,
    resolve_issue,
    search_issues,
    set_issue_status,
    update_issue,
)


STATUS_TAGS = {
    "Action Required": ("#fff2f0", "#9f1239"),
    "Monitoring": ("#fff8db", "#854d0e"),
    "Resolved": ("#ecfdf3", "#166534"),
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
        ttk.Label(profile, text="Current Worker").pack(side="left", padx=(0, 8))
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
        self.notebook.add(self.open_tab, text="Open Issues")
        self.notebook.add(self.entry_tab, text="New / Edit Issue")
        self.notebook.add(self.search_tab, text="Search & Excel Report")

        self.build_open_tab()
        self.build_entry_tab()
        self.build_search_tab()

    def build_open_tab(self) -> None:
        self.dashboard_frame = ttk.Frame(self.open_tab)
        self.dashboard_frame.pack(fill="x", pady=(0, 12))
        self.dashboard_vars: dict[str, tk.StringVar] = {}
        for title in ["Action Required", "Monitoring", "Resolved Today", "Active"]:
            self.add_dashboard_card(self.dashboard_frame, title)

        toolbar = ttk.Frame(self.open_tab)
        toolbar.pack(fill="x", pady=(0, 10))
        ttk.Button(toolbar, text="Refresh", command=self.refresh_open_issues).pack(side="left")
        ttk.Button(toolbar, text="Edit", command=self.load_selected_open_issue).pack(side="left", padx=8)
        ttk.Button(toolbar, text="Action Required", command=lambda: self.quick_status_selected(self.open_tree, "Action Required")).pack(side="left")
        ttk.Button(toolbar, text="Monitoring", command=lambda: self.quick_status_selected(self.open_tree, "Monitoring")).pack(side="left", padx=8)
        ttk.Button(toolbar, text="Resolve", command=self.resolve_selected_open_issue).pack(side="left")
        ttk.Button(toolbar, text="Delete Selected", command=lambda: self.delete_selected_issue(self.open_tree)).pack(side="left", padx=8)

        content = ttk.Frame(self.open_tab)
        content.pack(fill="both", expand=True)
        content.columnconfigure(0, weight=3)
        content.columnconfigure(1, weight=1)
        content.rowconfigure(0, weight=1)

        self.open_tree = self.make_issue_tree(content)
        self.open_tree.grid(row=0, column=0, sticky="nsew", padx=(0, 12))
        self.open_tree.bind("<Double-1>", lambda _event: self.load_selected_open_issue())
        self.open_tree.bind("<<TreeviewSelect>>", lambda _event: self.update_detail_panel(self.open_tree))

        self.detail_frame = ttk.Frame(content, style="Panel.TFrame", padding=14)
        self.detail_frame.grid(row=0, column=1, sticky="nsew")
        self.detail_frame.columnconfigure(0, weight=1)
        ttk.Label(self.detail_frame, text="Selected Issue", style="Subheader.TLabel").grid(row=0, column=0, sticky="w", pady=(0, 10))
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
            ttk.Label(self.detail_frame, text=label, style="Panel.TLabel").grid(row=row_index * 2 - 1, column=0, sticky="w", pady=(7, 0))
            ttk.Label(self.detail_frame, textvariable=variable, style="Panel.TLabel", wraplength=260).grid(row=row_index * 2, column=0, sticky="w")

        ttk.Label(self.detail_frame, text="Description", style="Panel.TLabel").grid(row=16, column=0, sticky="w", pady=(14, 0))
        self.detail_description = tk.Text(self.detail_frame, height=7, wrap="word", font=("Segoe UI", 9), state="disabled")
        self.detail_description.grid(row=17, column=0, sticky="nsew", pady=(3, 0))

    def add_dashboard_card(self, parent: ttk.Frame, title: str) -> None:
        card = ttk.Frame(parent, style="Card.TFrame", padding=12)
        card.pack(side="left", fill="x", expand=True, padx=(0, 10))
        value = tk.StringVar(value="0")
        self.dashboard_vars[title] = value
        ttk.Label(card, text=title, style="CardTitle.TLabel").pack(anchor="w")
        ttk.Label(card, textvariable=value, style="CardValue.TLabel").pack(anchor="w", pady=(4, 0))

    def build_entry_tab(self) -> None:
        panel = ttk.Frame(self.entry_tab, style="Panel.TFrame", padding=18)
        panel.pack(fill="both", expand=True)
        panel.columnconfigure(1, weight=1)
        panel.columnconfigure(3, weight=1)
        panel.rowconfigure(8, weight=1)

        ttk.Label(panel, text="Issue Record", style="Subheader.TLabel").grid(row=0, column=0, columnspan=4, sticky="w", pady=(0, 12))

        self.issue_date_var = tk.StringVar()
        self.issue_hour_var = tk.StringVar()
        self.issue_minute_var = tk.StringVar()
        self.resolved_time_var = tk.StringVar()
        self.line_var = tk.StringVar(value=LINES[0])
        self.instrument_var = tk.StringVar(value=INSTRUMENTS[0])
        self.category_var = tk.StringVar(value=CATEGORIES[0])
        self.subcategory_var = tk.StringVar(value=CATEGORY_MAP[CATEGORIES[0]][0])
        self.status_var = tk.StringVar(value=STATUS_OPTIONS[0])
        self.title_var = tk.StringVar()
        self.set_issue_datetime(now_text())

        self.add_line_instrument_grid(panel, 1)
        self.add_datetime_picker(panel, "Issue Time", 2, 0)
        self.add_labeled_combo(panel, "Line", self.line_var, LINES, 2, 2)
        self.add_labeled_combo(panel, "Instrument", self.instrument_var, INSTRUMENTS, 3, 0)
        category_combo = self.add_labeled_combo(panel, "Category", self.category_var, CATEGORIES, 3, 2)
        category_combo.bind("<<ComboboxSelected>>", lambda _event: self.update_subcategories())
        self.subcategory_combo = self.add_labeled_combo(panel, "Subcategory", self.subcategory_var, CATEGORY_MAP[self.category_var.get()], 4, 0)
        self.add_labeled_combo(panel, "Status", self.status_var, STATUS_OPTIONS, 4, 2)
        self.add_labeled_entry(panel, "Downtime Duration", self.resolved_time_var, 5, 0)
        self.add_labeled_entry(panel, "Title", self.title_var, 6, 0, columnspan=3)

        ttk.Label(panel, text="Description", style="Panel.TLabel").grid(row=7, column=0, sticky="nw", pady=7)
        self.description_text = tk.Text(panel, height=7, wrap="word", font=("Segoe UI", 10))
        self.description_text.grid(row=7, column=1, columnspan=3, sticky="nsew", pady=7)

        ttk.Label(panel, text="Resolution Notes", style="Panel.TLabel").grid(row=8, column=0, sticky="nw", pady=7)
        self.resolution_text = tk.Text(panel, height=5, wrap="word", font=("Segoe UI", 10))
        self.resolution_text.grid(row=8, column=1, columnspan=3, sticky="nsew", pady=7)

        actions = ttk.Frame(panel, style="Panel.TFrame")
        actions.grid(row=10, column=0, columnspan=4, sticky="ew", pady=(14, 0))
        ttk.Button(actions, text="New Blank Form", command=self.clear_form).pack(side="left")
        ttk.Button(actions, text="Delete Issue", command=self.delete_loaded_issue).pack(side="left", padx=8)
        ttk.Button(actions, text="Save Issue", style="Accent.TButton", command=self.save_issue).pack(side="right")

    def add_line_instrument_grid(self, parent: ttk.Frame, row: int) -> None:
        grid = ttk.Frame(parent, style="Panel.TFrame")
        grid.grid(row=row, column=0, columnspan=4, sticky="ew", pady=(0, 12))
        ttk.Label(grid, text="Line / Instrument", style="Panel.TLabel").grid(row=0, column=0, sticky="w", padx=(0, 8), pady=3)
        for column_index, instrument in enumerate(INSTRUMENTS, start=1):
            ttk.Label(grid, text=instrument, style="Panel.TLabel", anchor="center").grid(row=0, column=column_index, padx=2, pady=3)
        for row_index, line in enumerate(LINES, start=1):
            ttk.Label(grid, text=line, style="Panel.TLabel").grid(row=row_index, column=0, sticky="w", padx=(0, 8), pady=2)
            for column_index, instrument in enumerate(INSTRUMENTS, start=1):
                ttk.Button(
                    grid,
                    text="Select",
                    width=8,
                    command=lambda selected_line=line, selected_instrument=instrument: self.select_line_instrument(
                        selected_line, selected_instrument
                    ),
                ).grid(row=row_index, column=column_index, padx=2, pady=2)

    def select_line_instrument(self, line: str, instrument: str) -> None:
        self.line_var.set(line)
        self.instrument_var.set(instrument)

    def build_search_tab(self) -> None:
        filters = ttk.Frame(self.search_tab, style="Panel.TFrame", padding=14)
        filters.pack(fill="x", pady=(0, 10))

        self.filter_status = tk.StringVar()
        self.filter_line = tk.StringVar()
        self.filter_instrument = tk.StringVar()
        self.filter_category = tk.StringVar()
        self.filter_subcategory = tk.StringVar()
        self.filter_keyword = tk.StringVar()
        self.filter_from = tk.StringVar()
        self.filter_to = tk.StringVar()

        self.add_filter_combo(filters, "Status", self.filter_status, [""] + STATUS_OPTIONS, 0, 0)
        self.add_filter_combo(filters, "Line", self.filter_line, [""] + LINES, 0, 2)
        self.add_filter_combo(filters, "Instrument", self.filter_instrument, [""] + INSTRUMENTS, 0, 4)
        category_filter = self.add_filter_combo(filters, "Category", self.filter_category, [""] + CATEGORIES, 1, 0)
        category_filter.bind("<<ComboboxSelected>>", lambda _event: self.update_filter_subcategories())
        self.filter_subcategory_combo = self.add_filter_combo(filters, "Subcategory", self.filter_subcategory, [""], 1, 2)
        self.add_filter_entry(filters, "Keyword", self.filter_keyword, 1, 4)
        self.add_filter_entry(filters, "From", self.filter_from, 2, 0)
        self.add_filter_entry(filters, "To", self.filter_to, 2, 2)

        quick_filters = ttk.Frame(filters, style="Panel.TFrame")
        quick_filters.grid(row=3, column=0, columnspan=6, sticky="w", pady=(8, 0))
        ttk.Button(quick_filters, text="Today", command=lambda: self.apply_quick_filter("today")).pack(side="left", padx=(0, 6))
        ttk.Button(quick_filters, text="This Week", command=lambda: self.apply_quick_filter("week")).pack(side="left", padx=(0, 6))
        ttk.Button(quick_filters, text="Action Required", command=lambda: self.apply_quick_filter("action")).pack(side="left", padx=(0, 6))
        ttk.Button(quick_filters, text="Monitoring", command=lambda: self.apply_quick_filter("monitoring")).pack(side="left", padx=(0, 6))
        ttk.Button(quick_filters, text="Camera Grab Fail", command=lambda: self.apply_quick_filter("camera_grab")).pack(side="left", padx=(0, 6))
        ttk.Button(quick_filters, text="Recipe Issues", command=lambda: self.apply_quick_filter("recipe")).pack(side="left", padx=(0, 6))
        ttk.Button(quick_filters, text="Clear", command=self.clear_search_filters).pack(side="left")

        buttons = ttk.Frame(filters, style="Panel.TFrame")
        buttons.grid(row=2, column=4, columnspan=2, sticky="e", padx=6, pady=6)
        ttk.Button(buttons, text="Search", style="Accent.TButton", command=self.search_records).pack(side="left", padx=(0, 8))
        ttk.Button(buttons, text="Export Excel", command=self.export_search_results).pack(side="left")
        ttk.Button(buttons, text="Delete Selected", command=lambda: self.delete_selected_issue(self.search_tree)).pack(side="left", padx=(8, 0))

        self.search_tree = self.make_issue_tree(self.search_tab)
        self.search_tree.pack(fill="both", expand=True)
        self.search_tree.bind("<Double-1>", lambda _event: self.load_selected_search_issue())

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
            "instrument": 120,
            "category": 110,
            "subcategory": 170,
            "title": 290,
            "status": 100,
            "worker": 130,
        }
        for column in columns:
            tree.heading(column, text=headings[column])
            tree.column(column, width=widths[column], anchor="w")
        for status, (background, foreground) in STATUS_TAGS.items():
            tree.tag_configure(status, background=background, foreground=foreground)
        return tree

    def add_labeled_entry(self, parent: ttk.Frame, label: str, variable: tk.StringVar, row: int, column: int, columnspan: int = 1) -> ttk.Entry:
        ttk.Label(parent, text=label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(0, 8), pady=7)
        entry = ttk.Entry(parent, textvariable=variable)
        entry.grid(row=row, column=column + 1, columnspan=columnspan, sticky="ew", pady=7)
        return entry

    def add_datetime_picker(self, parent: ttk.Frame, label: str, row: int, column: int) -> None:
        ttk.Label(parent, text=label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(0, 8), pady=7)
        frame = ttk.Frame(parent, style="Panel.TFrame")
        frame.grid(row=row, column=column + 1, sticky="w", pady=7)
        self.issue_date_entry = ttk.Entry(frame, textvariable=self.issue_date_var, width=12)
        self.issue_date_entry.pack(side="left", padx=(0, 10))
        self.issue_date_entry.bind("<Button-1>", self.open_calendar_popup)
        self.issue_date_entry.bind("<FocusIn>", self.open_calendar_popup)
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
        self.position_calendar_popup(popup)

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

    def position_calendar_popup(self, popup: tk.Toplevel) -> None:
        self.issue_date_entry.update_idletasks()
        x = self.issue_date_entry.winfo_rootx()
        y = self.issue_date_entry.winfo_rooty() + self.issue_date_entry.winfo_height()
        popup.geometry(f"+{x}+{y}")

    def add_labeled_combo(self, parent: ttk.Frame, label: str, variable: tk.StringVar, values: list[str], row: int, column: int) -> ttk.Combobox:
        ttk.Label(parent, text=label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(0, 8), pady=7)
        combo = ttk.Combobox(parent, textvariable=variable, values=values, state="readonly")
        combo.grid(row=row, column=column + 1, sticky="ew", pady=7)
        return combo

    def add_filter_combo(self, parent: ttk.Frame, label: str, variable: tk.StringVar, values: list[str], row: int, column: int) -> ttk.Combobox:
        ttk.Label(parent, text=label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(4, 8), pady=6)
        combo = ttk.Combobox(parent, textvariable=variable, values=values, state="readonly", width=20)
        combo.grid(row=row, column=column + 1, sticky="ew", padx=(0, 12), pady=6)
        return combo

    def add_filter_entry(self, parent: ttk.Frame, label: str, variable: tk.StringVar, row: int, column: int) -> ttk.Entry:
        ttk.Label(parent, text=label, style="Panel.TLabel").grid(row=row, column=column, sticky="w", padx=(4, 8), pady=6)
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
            resolved_time=self.resolved_time_var.get().strip(),
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
        self.resolved_time_var.set("")
        self.line_var.set(LINES[0])
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
            self.filter_instrument,
            self.filter_category,
            self.filter_subcategory,
            self.filter_keyword,
            self.filter_from,
            self.filter_to,
        ]:
            variable.set("")
        self.update_filter_subcategories()
        self.search_records()

    def populate_tree(self, tree: ttk.Treeview, rows: list) -> None:
        for item in tree.get_children():
            tree.delete(item)
        for row in rows:
            tree.insert(
                "",
                "end",
                values=(
                    row["id"],
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
        values = tree.item(selection[0], "values")
        return int(values[0])

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
            values = tree.item(item, "values")
            if values and int(values[0]) == issue_id:
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
        if not messagebox.askyesno(APP_TITLE, "Delete this issue log?"):
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
