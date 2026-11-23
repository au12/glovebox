# Changelog

## Unreleased: CANary diagnostics (branch `CANary`)

A new section for every vehicle, fed by a [CANary](https://github.com/au12/CANary) device in the car's diagnostic port.

```
ghcr.io/au12/glovebox:canary
```

- **Odometer records from the car.** Each check-in records the reading: one record a day, tagged `canary`, only ever upward.
- **A weekly report** built by Glovebox from the raw data the device uploads: a plain-language verdict, findings ordered by severity with limit gauges, week-over-week trends and explanations, trouble codes with their meaning, and what changed since the week before.
- **Drives and raw data.** Every drive listed by day; open one to chart any logged reading or download its log.
- **Prints as a report,** explanations included.
- **ntfy notifications** when a warning, a critical finding or a stored code first appears, and a short weekly summary.
- **Settings per vehicle:** odometer unit and correction, first day of the week, how long raw data is kept, notifications, muting a check, analysing history again.
- Data lives in `data/canary`; it works with LiteDB and PostgreSQL alike and does not touch the database schema.
- The image workflow publishes this branch as `canary` and runs the analysis self-test first.

## 0.2.0 (2026-11-15)

A rebuilt maintenance report.

```
ghcr.io/au12/glovebox:0.2.0
```

- **Printing the maintenance report works again.** Since the redesign, printing it also printed the vehicle page around it. Now only the report prints, on white paper even in dark mode. Record stickers print cleanly too.
- **A summary page up front:** the vehicle and its photo, the odometer, distance logged, fuel economy and cost per mile, what's coming up, where the money went by record type and by year, total cost of ownership, and when routine items were last done.
- **A service history built for paper:** records grouped by year with counts and totals, a separate column for parts and notes that wraps instead of cutting off, color-coded record types, column headings repeated on every page, and page numbers in the footer.
- **Hide cost tracking applies to the report.** With it on, every amount, total and cost column is left out.
- Column choices, extra fields, tag filters, date ranges and individual record printing still work as before.

## 0.1.0 (2026-11-15)

The first Glovebox release: a redesigned [LubeLogger](https://github.com/hargata/lubelog) that puts what's due next front and center. It reads and writes the same data as LubeLogger 1.7.4, so an existing install can switch images (and back) without migrating anything.

## Image

```
ghcr.io/au12/glovebox:0.1.0
```

Built for `linux/amd64` and `linux/arm64`. `latest` follows `main`.

**Switching from LubeLogger on Unraid:** edit the container, change Repository to `ghcr.io/au12/glovebox:0.1.0` (or `:latest`), leave every path, port and variable as it is, and apply. A template for new installs is in `templates/glovebox.xml`.

## Highlights

- **Garage** with a status summary, a "Needs attention" list across all vehicles, and cards showing each vehicle's odometer, status and next two due items.
- **Vehicle overview** built around what's coming up: progress through each interval, an estimate of when it'll be due at your driving pace, and one-tap **Log it**, which prefills a service record and rolls recurring reminders forward. Recent history, key stats and pinned notes sit alongside. Charts moved to their own page.
- **Phone-first layout**: bottom navigation, quick add for every record type, bottom-sheet forms and card lists.
- **Notes** open as rendered markdown with their attachments.
- **Attachment viewer** for images, PDFs, video, audio, text, CSV and markdown, right in the app.
- **ntfy push notifications** for reminders: heads-up, due soon, urgent and overdue, each sent once per due cycle, with quiet hours and optional overdue repeats.
- **Heads-up before it's due**: a default early warning in distance and days, adjustable or switchable off per reminder.
- **Hide cost tracking** with one switch. Stored amounts are kept.
- **Reorganized settings** that save as you go.
- **Search removed**; tag filters remain.

## Fixes

- Custom urgency thresholds on one reminder no longer leak into the reminders after it.
- Forms opened from links could reopen or clear themselves when two page loads overlapped.
- Attachment names with apostrophes or ampersands no longer break previews or renames.
- Numbers with several thousands separators sort correctly.
- The reminder chart shows overdue in red.

## Known limitations

- Text added by Glovebox is English only.
- The kiosk and error pages keep the LubeLogger look.

Glovebox is MIT licensed, like LubeLogger by Hargata Softworks.
