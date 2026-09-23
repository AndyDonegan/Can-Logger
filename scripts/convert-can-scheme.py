#!/usr/bin/env python3
"""Export the Data sheet to the application's CSV using only the Python standard library."""
import argparse
import csv
from collections import Counter
from datetime import datetime, timedelta
from pathlib import Path
import posixpath
import re
import xml.etree.ElementTree as ET
from zipfile import ZipFile

NS = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
COLUMNS = ["CanID", "Description", "Bit", "Variable", "Function", "Options", "History"]


def convert(source, destination):
    with ZipFile(source) as workbook:
        sheets = ET.fromstring(workbook.read("xl/workbook.xml"))
        sheet = next(s for s in sheets.findall("s:sheets/s:sheet", NS) if s.get("name") == "Data")
        relationships = ET.fromstring(workbook.read("xl/_rels/workbook.xml.rels"))
        target = next(r.get("Target") for r in relationships if r.get("Id") == sheet.get(f"{{{REL}}}id"))
        sheet_path = target.lstrip("/") if target.startswith("/") else posixpath.normpath("xl/" + target)
        strings = []
        if "xl/sharedStrings.xml" in workbook.namelist():
            strings = ["".join(t.text or "" for t in item.findall(".//s:t", NS))
                       for item in ET.fromstring(workbook.read("xl/sharedStrings.xml"))]
        styles = ET.fromstring(workbook.read("xl/styles.xml"))
        formats = {int(f.get("numFmtId")): f.get("formatCode")
                   for f in styles.findall("s:numFmts/s:numFmt", NS)}
        cell_styles = styles.find("s:cellXfs", NS)
        rows = []
        headers = None
        for row in ET.fromstring(workbook.read(sheet_path)).findall("s:sheetData/s:row", NS):
            cells = {}
            for cell in row.findall("s:c", NS):
                if cell.find("s:f", NS) is not None:
                    raise ValueError(f"Formula in {cell.get('r')}: export calculated values first")
                value = cell.find("s:v", NS)
                text = value.text or "" if value is not None else ""
                if cell.get("t") == "s":
                    text = strings[int(text)]
                elif cell.get("t") == "inlineStr":
                    text = "".join(t.text or "" for t in cell.findall(".//s:t", NS))
                elif text and cell.get("t") not in ("str", "b", "e"):
                    style = cell_styles[int(cell.get("s", "0"))]
                    format_id = int(style.get("numFmtId", "0"))
                    if formats.get(format_id) == r"d\-m":
                        # Excel stored a range-like value as a date; retain its displayed text.
                        if sheets.find("s:workbookPr", NS).get("date1904") in ("1", "true"):
                            raise ValueError("1904 date system is not supported")
                        date = datetime(1899, 12, 30) + timedelta(days=float(text))
                        text = f"{date.day}-{date.month}"
                    elif format_id != 0:
                        raise ValueError(f"Unsupported numeric format in {cell.get('r')}: {format_id}")
                if cell.get("t") == "e":
                    raise ValueError(f"Excel error in {cell.get('r')}: {text}")
                if text:
                    cells[re.match(r"[A-Z]+", cell.get("r"))[0]] = text
            if not cells:
                continue
            if headers is None:
                headers = {column: name.strip() for column, name in cells.items()}
                if Counter(headers.values()) != Counter(COLUMNS):
                    raise ValueError(f"Unexpected headers: {headers}")
                continue
            if cells.keys() - headers.keys():
                raise ValueError(f"Unexpected populated columns in row {row.get('r')}")
            record = {name: cells.get(column, "") for column, name in headers.items()}
            if not record["CanID"].isdigit() or not 0 <= int(record["CanID"]) <= 0x1FFFFFFF:
                raise ValueError(f"Invalid CAN ID in row {row.get('r')}")
            if not record["Bit"].isdigit() or not 0 <= int(record["Bit"]) <= 7:
                raise ValueError(f"Invalid byte index in row {row.get('r')}")
            rows.append(record)
    if not rows:
        raise ValueError("No CAN definitions found")
    rows.sort(key=lambda r: (int(r["CanID"]), int(r["Bit"])))
    with destination.open("w", encoding="utf-8-sig", newline="") as output:
        writer = csv.DictWriter(output, fieldnames=COLUMNS)
        writer.writeheader()
        writer.writerows(rows)
    # Check that CSV quoting preserves every field, including multiline History.
    with destination.open(encoding="utf-8-sig", newline="") as output:
        assert list(csv.DictReader(output)) == rows, "CSV round-trip mismatch"
    print(f"Exported {len(rows)} rows, {len({r['CanID'] for r in rows})} IDs, "
          f"{sum(bool(r['History']) for r in rows)} History entries to {destination}")
    duplicates = {key: count for key, count in Counter((r["CanID"], r["Bit"]) for r in rows).items() if count > 1}
    if duplicates:
        print(f"Preserved duplicate (ID, byte) definitions for review: {duplicates}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workbook", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.workbook.resolve() == args.output.resolve():
        parser.error("Workbook and output must be different files")
    convert(args.workbook, args.output)
