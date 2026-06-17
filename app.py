from __future__ import annotations

import calendar
import tkinter as tk
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from vision_tracker import (
    APP_TITLE,
    CATEGORIES,
    CATEGORY_MAP,
    INSTRUMENTS,
    LINES,
    STATUS_OPTIONS,
    WORKERS,
    IssueInput,
    create_issue,
    export_issues_to_excel,
    get_issue,
    initialize_database,
    now_text,
    resolve_issue,
    search_issues,
    update_issue,
)


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
        toolbar = ttk.Frame(self.open_tab)
        toolbar.pack(fill="x", pady=(0, 10))
        ttk.Button(toolbar, text="Refresh", command=self.refresh_open_issues).pack(side="left")
        ttk.Button(toolbar, text="Edit Selected", command=self.load_selected_open_issue).pack(side="left", padx=8)
        ttk.Button(toolbar, text="Resolve Selected", command=self.resolve_selected_open_issue).pack(side="left")

        self.open_tree = self.make_issue_tree(self.open_tab)
        self.open_tree.pack(fill="both", expand=True)
        self.open_tree.bind("<Double-1>", lambda _event: self.load_selected_open_issue())

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

        self.add_datetime_picker(panel, "Issue Time", 1, 0)
        self.add_labeled_combo(panel, "Line", self.line_var, LINES, 1, 2)
        self.add_labeled_combo(panel, "Instrument", self.instrument_var, INSTRUMENTS, 2, 0)
        category_combo = self.add_labeled_combo(panel, "Category", self.category_var, CATEGORIES, 2, 2)
        category_combo.bind("<<ComboboxSelected>>", lambda _event: self.update_subcategories())
        self.subcategory_combo = self.add_labeled_combo(panel, "Subcategory", self.subcategory_var, CATEGORY_MAP[self.category_var.get()], 3, 0)
        self.add_labeled_combo(panel, "Status", self.status_var, STATUS_OPTIONS, 3, 2)
        self.add_labeled_entry(panel, "Downtime Duration", self.resolved_time_var, 4, 0)
        self.add_labeled_entry(panel, "Title", self.title_var, 5, 0, columnspan=3)

        ttk.Label(panel, text="Description", style="Panel.TLabel").grid(row=6, column=0, sticky="nw", pady=7)
        self.description_text = tk.Text(panel, height=7, wrap="word", font=("Segoe UI", 10))
        self.description_text.grid(row=6, column=1, columnspan=3, sticky="nsew", pady=7)

        ttk.Label(panel, text="Resolution Notes", style="Panel.TLabel").grid(row=7, column=0, sticky="nw", pady=7)
        self.resolution_text = tk.Text(panel, height=5, wrap="word", font=("Segoe UI", 10))
        self.resolution_text.grid(row=7, column=1, columnspan=3, sticky="nsew", pady=7)

        actions = ttk.Frame(panel, style="Panel.TFrame")
        actions.grid(row=9, column=0, columnspan=4, sticky="ew", pady=(14, 0))
        ttk.Button(actions, text="New Blank Form", command=self.clear_form).pack(side="left")
        ttk.Button(actions, text="Save Issue", style="Accent.TButton", command=self.save_issue).pack(side="right")

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

        buttons = ttk.Frame(filters, style="Panel.TFrame")
        buttons.grid(row=2, column=4, columnspan=2, sticky="e", padx=6, pady=6)
        ttk.Button(buttons, text="Search", style="Accent.TButton", command=self.search_records).pack(side="left", padx=(0, 8))
        ttk.Button(buttons, text="Export Excel", command=self.export_search_results).pack(side="left")

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
        ttk.Entry(frame, textvariable=self.issue_date_var, width=12).pack(side="left")
        ttk.Button(frame, text="Calendar", command=self.open_calendar_popup).pack(side="left", padx=(6, 10))
        tk.Spinbox(frame, from_=0, to=23, textvariable=self.issue_hour_var, width=3, wrap=True, format="%02.0f").pack(side="left")
        ttk.Label(frame, text=":", style="Panel.TLabel").pack(side="left", padx=3)
        tk.Spinbox(frame, from_=0, to=59, textvariable=self.issue_minute_var, width=3, wrap=True, format="%02.0f").pack(side="left")

    def open_calendar_popup(self) -> None:
        popup = tk.Toplevel(self)
        popup.title("Select Issue Date")
        popup.resizable(False, False)
        popup.transient(self)
        popup.grab_set()

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
        self.status_var.set("Open")
        self.title_var.set("")
        self.description_text.delete("1.0", "end")
        self.resolution_text.delete("1.0", "end")

    def refresh_open_issues(self) -> None:
        rows = search_issues({"status": "Open"})
        self.populate_tree(self.open_tree, rows)

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
