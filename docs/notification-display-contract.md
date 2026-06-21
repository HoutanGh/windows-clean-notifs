# Notification Display Contract

This document defines the V1 derived display shape for stored Windows notifications.

The collector must preserve raw Windows notification data. User-facing cleanup happens only when deriving display fields for terminal output, a future API, SSE, or React UI.

## Raw Versus Display Data

Raw data stays unchanged during capture and storage:

- raw title;
- raw body/message;
- raw text elements in order;
- app ID / AUMID;
- Windows notification ID;
- Windows creation timestamp;
- captured timestamp.

The display layer derives a smaller model:

```text
sourceApp
timestamp
primaryText
messageText
```

Display mapping:

- `sourceApp`: app display name.
- `timestamp`: Windows notification creation timestamp.
- `primaryText`: notification title.
- `messageText`: notification body.

## Fallback Rules

- Trim only leading and trailing whitespace for display.
- Normalize display line endings to `\n`.
- Hide empty `primaryText` and `messageText`.
- If title is missing, use the first non-empty raw text element as `primaryText`.
- If body is missing, use the remaining non-empty raw text elements in order as `messageText`, joined with newlines.
- Preserve multiline structure when combining elements.
- If `primaryText` and `messageText` are exact duplicates after trimming, show the content once as `primaryText`.
- Optional source-aware display metadata may be derived after the generic display fields are mapped.

The display mapper must preserve:

- punctuation;
- URLs;
- emoji;
- Unicode symbols;
- stock tickers;
- prices;
- percentages;
- line breaks.

Do not broadly strip question marks or non-ASCII characters.

## Normal Terminal Output

With `--listen --print-content`, enabled-source notifications print in concise user-facing form:

```text
14:32:08 · Discord
Scanner Bot
NVDA breaking premarket high
```

Normal notification output does not include:

- app ID;
- Windows notification ID;
- raw text element indexes;
- poll/startup event labels;
- enabled status;
- separate creation/captured timestamp diagnostics.

Source-discovered messages may still include app display name and app ID because the app ID is needed for `--enable-source` and `--disable-source`.

## Debug Raw Output

`--debug-raw` is available only with `--print-content`.

With `--listen --print-content --debug-raw`, each printed enabled-source notification includes the concise display output plus a debug section containing:

- app ID;
- Windows notification ID;
- raw title;
- raw body;
- raw text elements with indexes;
- derived primary text;
- derived message text;
- suspicious Unicode code points.

Debug raw output is opt-in because it can expose private notification content.

Suspicious code-point diagnostics report:

- literal question marks as `U+003F`;
- replacement characters as `U+FFFD`;
- non-ASCII Unicode, including emoji and formatting marks.

This is enough to distinguish a literal `?`, a replacement character, and valid Unicode that the terminal may render poorly.

## Question-Mark Finding

The existing local SQLite database was inspected without printing private notification text. The inspection looked at code-point statistics only.

Result from the inspected stored rows:

- no literal question mark `U+003F` was found in the inspected stored title/raw fields;
- no replacement character `U+FFFD` was found;
- stored non-ASCII code points included `U+2068` FIRST STRONG ISOLATE and `U+2069` POP DIRECTIONAL ISOLATE;
- one inspected value also contained `U+2074` SUPERSCRIPT FOUR.

Conclusion: the observed `?` characters in earlier terminal output were not proven to be destroyed notification content. In the inspected stored rows, the notable characters were valid Unicode code points, especially invisible bidirectional isolation marks that some terminals or fonts may render as question marks.

Remaining limitation: this finding applies only to the inspected stored rows. Future notifications can still contain literal `?` punctuation, `U+FFFD`, emoji, or other valid symbols, so the display layer must preserve them.

## Implementation Boundary

Cleanup belongs in a shared display mapper when reading notification data for display.

Discord context is derived separately from raw stored notification fields when reading for API, SSE, or UI display. It is best-effort presentation metadata only:

- use it for server tabs and channel columns in the Discord view;
- preserve generic `primaryText` and `messageText`;
- return an unknown/fallback context when Discord text cannot be parsed;
- never hide a notification because parsing failed.

Do not:

- mutate captured notification text;
- rewrite stored notification text;
- migrate the database just for display cleanup;
- require Discord-specific fields for capture, storage, deduplication, retention, or source selection.

Keep raw/debug fields available for developer diagnostics, but keep normal output compact and generic.
