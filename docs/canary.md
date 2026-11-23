# CANary in Glovebox

How the CANary section works behind the page: what a device sends, what Glovebox keeps, and how a
week of drives becomes a report. The device side is documented in the
[CANary repository](https://github.com/au12/CANary).

## What happens when the car comes home

1. The device joins Wi-Fi and asks `GET /api/canary/hello`. A reply that contains `canary_api`
   tells it this server takes drives as well as odometer readings. (Against a plain LubeLogger
   server the device falls back to adding odometer records through the standard API.)
2. It checks in: who it is, the odometer, the battery, what it found on the last drive. Glovebox
   records the odometer and answers with the time and whether it already holds the vehicle
   profile the device is using.
3. If the profile is new, the device uploads it once.
4. Each drive not yet on the server is uploaded in 64 KB pieces. An upload that is cut off
   resumes where it stopped the next time.
5. When the last piece of a drive arrives, Glovebox compresses the log, analyses it, and sends a
   notification if something new turned up.

A week after a drive is safely on the server, the device deletes its own copy.

## Device API

All requests carry the API key in an `x-api-key` header when the server has a login. A key needs
the Editor permission; **CANary > Create a key for the device** makes one. Without a login no key
is needed. A missing or wrong key is answered the way Glovebox answers every API call: with a
redirect to the login page.

| Request | Purpose | Reply |
| --- | --- | --- |
| `GET /api/canary/hello` | Is this a CANary-aware server? | `{"canary_api":1,"server":"Glovebox","version":"0.2.0"}` |
| `POST /api/canary/checkin?vehicleId=N` | Check in; JSON body below | `{"ok":true,"have_profile":true,"time":1795412906,"odometer":{"recorded":true,"value":103002,"unit":"mi"}}` |
| `PUT /api/canary/profile?hash=H` | Store the resolved vehicle profile (JSON body, at most 1 MB). `H` is the FNV-1a 32-bit hash of the body in hex | `{"ok":true}` |
| `GET /api/canary/trips/{id}/status?vehicleId=N` | How much of this drive is on the server | `{"ok":true,"received":65536,"complete":false}` |
| `POST /api/canary/trips/{id}/chunk?vehicleId=N&offset=O&total=T&crc=C&device=D&start=S` | One piece of the raw log as the request body | `{"ok":true,"received":131072,"complete":false}` |

Errors come back as `{"ok":false,"error":"..."}` with status 400, 404 (no such vehicle), 413 (piece
too large) or 500.

**Check-in body.** Every field is optional; a server reads what it understands and ignores the rest:

```json
{
  "device": "canary-51m0a7", "name": "", "fw": "0.1.0", "board": "wican-pro",
  "vin": "3VV2B7AX5JM012345",
  "profile": { "id": "vw-tiguan-mk2", "name": "Volkswagen Tiguan (2018+, 2.0 TSI)", "hash": "01d70daf" },
  "odometer_km": 165766.4, "odometer_time": 1795409950,
  "battery_v": 12.6, "state": "parked", "time": 1795412906, "tz_offset_min": -300, "pending_trips": 1,
  "storage": { "total": 31914983424, "free": 31902400512 },
  "worst": "warning", "mil": false,
  "findings": [ { "id": "boost_under_target", "status": "warning", "value": 33.1, "unit": "kPa" } ],
  "dtcs": [ { "code": "P0299", "kind": "pending", "module": "Engine (OBD-II)" } ]
}
```

**Uploading a drive.** `offset` must equal the number of bytes the server already holds; if it
does not, the reply's `received` says where to continue. `crc` is the CRC-32 of the piece (the
usual zlib polynomial) in hex, `total` the size of the whole log, `start` the drive's start time
in Unix seconds when the device knows it. The device sends 64 KB pieces; the server accepts
pieces up to 8 MB and drives up to 256 MB. Trip ids are 6 to 48 letters, digits and hyphens.

## What is kept

Everything lives under `data/canary` in the data folder (`/App/data/canary` in the container). The
database is not touched, so it makes no difference whether Glovebox runs on LiteDB or PostgreSQL.

```
data/canary/
  profiles/<hash>.json          vehicle profiles, shared by every vehicle that uses them
  v<vehicle>/
    device.json                 the last check-in
    settings.json               this vehicle's CANary settings
    alerts.json                 which notifications have gone out
    trips/<id>.cnl.part         an upload in progress
    trips/<id>.cnl.gz           the raw log of a drive, compressed
    trips/<id>.json             the analysis of that drive
```

A drive's raw log runs from a few hundred kilobytes to a couple of megabytes compressed. **Keep raw
data** in the settings removes logs older than the chosen number of months; the analysis files
stay, so reports and trends are unaffected, but those drives can no longer be charted or analysed
again. Deleting a vehicle removes its folder at the next hourly housekeeping run.

The built-in **Download backup** does not include `data/canary`. Back up the data folder itself.

## From drives to a weekly report

**Each drive** is replayed through the health checks of the vehicle profile it was recorded with:
the same expression language, the same statistics and the same limits the firmware uses. The
replay runs on an even half-second grid and honours each reading's shelf life, exactly as the
device does while driving. The result is stored next to the log, together with the internal state
of every check (how long its conditions held, the running statistics).

**A week** is not the worst of its drives. The stored states of all the week's drives are merged
as if they were one long drive, and the limits are applied to that. A check that needs ninety
seconds of hard acceleration is judged from the week's total even when no single commute had
enough. Checks that only make sense per drive (time to warm up, counted events, a change over the
drive) report the worst drive instead.

Under each finding the report says how many drives it showed up on, and compares the week's figure
with up to eight weeks before it. Findings are marked **New**, **Worse** or **Cleared** against
the most recent earlier week that had a verdict for that check.

The headline counts the findings, plus one if any trouble codes were seen. Its colour is that of
the most serious item: stored and permanent codes count as warnings, pending codes as advisories.

**Weeks and time.** A week runs Monday to Sunday, or Sunday to Saturday if set. Days are taken in
the server's time zone (the `TZ` variable of the container). If the server runs in UTC, the offset
the device was set to is used instead. A drive recorded before the device ever learned the time
is filed under the day it was uploaded.

## Odometer

Every check-in carries the odometer in kilometres. Glovebox converts it to the unit the vehicle's
records are kept in (miles when fuel economy is shown in MPG, kilometres otherwise; fixed at the
first check-in and changeable in the settings), adds the correction if one is set, and writes one
record a day tagged `canary`. Later readings on the same day move that record forward. A reading
lower than the latest record is never written.

## Notifications

Sent through ntfy to everyone who has notifications turned on in their Glovebox settings and can
see the vehicle.

- A message when a warning or critical finding, or a stored trouble code, first appears on a
  drive. The same finding is not repeated for fourteen days unless it gets more serious.
- Drives that are already more than three days old when they arrive do not trigger messages.
- Muted checks never notify.
- A short summary of the finished week, sent after 8 in the morning on the first day of the next.

## Keeping in step with the firmware

`Logic/Canary` is a port of the firmware's analysis. `tools/CanarySelfTest` checks it three ways:
the expression test vectors shared with the firmware, a recorded drive whose verdicts the device
computed itself, and the rule that merging one drive changes nothing. It runs before every image
build:

```
dotnet run --project tools/CanarySelfTest
```

To refresh the fixtures after a firmware change, record a drive with the simulator in the CANary
repository and copy the log, its summary and the resolved profile into
`tools/CanarySelfTest/fixtures`.
