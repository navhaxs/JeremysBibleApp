# Import ZIP of USX Files as Custom Translation

**Date:** 2026-08-09
**Status:** Approved

## Goal

Let users import their own translation (custom/private text, not available online) as a ZIP of USX files, install it alongside the existing online BSB source, and switch between installed translations. Reuses the existing `UsxBibleParser` unchanged; adds an extraction/validation/switching layer around it.

## Current pipeline (for reference)

- `UsxBibleApiLoader` fetches BSB from `https://v1.fetch.bible/bibles/eng_bsb/usx/`, disk-caches to `%APPDATA%\MyBibleApp\UsxCache\{code}.usx`
- `BibleContentService` (singleton) owns the loader, exposes `LoadBookAsync(bookCode)`
- `UsxBibleParser.Parse(XDocument) : BibleBook` is the sole USX→model entry point, stateless, called once per book file
- `ScriptureViewModel` calls through `BibleContentService` and renders `BibleBook.Paragraphs`
- Book/chapter/verse metadata (`Assets/books.json`, `Assets/last_verse.json`) is fixed to the standard 66-book Protestant canon — imported translations are assumed to match this canon; no per-translation versification support

## Architecture

New pieces, no changes to `UsxBibleParser`, `books.json`, or `last_verse.json`:

- **`UsxBibleZipLoader : IUsxBibleLoader`** — given a translation's extracted folder, lazy-parses a requested book via the existing `UsxBibleParser`, same disk-read-then-parse shape as `UsxBibleApiLoader`'s cache-hit path.
- **`TranslationManager`** (new service) — tracks installed translations (BSB-online + N imported), holds the active translation id, persists active id via the existing `_localStorageProvider` pattern used for `IsDebugMode`/`IsTabBarVisible`.
- **`BibleContentService.LoadBookAsync(bookCode, translationId)`** — routes to `UsxBibleApiLoader` for id `"bsb-online"`, or to the right `UsxBibleZipLoader` instance otherwise.

## Storage layout

```
%APPDATA%\MyBibleApp\Translations\{translationId}\
    manifest.json      { displayName, sourceZipName, importedAt, bookCodes: [...], missingBooks: [...] }
    gen.usx, exo.usx, ...   (extracted, keyed by lowercase 3-letter code read from <book code>, not filename)
```

`translationId` is a new GUID per import (avoids collisions on re-import/rename). BSB keeps its existing `UsxCache` folder untouched, fixed id `"bsb-online"`, always present, not deletable.

## Import flow

1. User picks a `.zip` via `StorageProvider.OpenFilePickerAsync` (new Avalonia file-picker usage — no prior precedent in this codebase).
2. Prompt for a display name (default: zip filename minus extension).
3. Extract to a temp directory first — never directly into `Translations\{id}\` — so a bad import doesn't leave partial state.
4. **Zip-slip guard:** for every entry, resolve the full extraction path and reject the entry if it escapes the temp dir (`Path.GetFullPath` + prefix check) before writing it.
5. **Size guard:** reject the whole import if total uncompressed size exceeds a cap (200MB) — cheap zip-bomb/DoS protection.
6. Walk extracted `.usx` files; for each, run it through `UsxBibleParser.Parse` and read the resulting `BibleBook`'s book code (not the filename — `Parse` already reads `<book code="...">` internally, at line 31 of `UsxBibleParser.cs`). Skip unreadable/non-USX entries individually (log, don't abort).
7. Compare the discovered book-code set against the 66 canonical codes from `books.json`:
   - **No missing books:** proceed.
   - **Some missing:** show a warning dialog listing missing books; user may accept (partial translation — those books show as unavailable) or cancel.
   - **Zero valid books found:** hard error, abort, delete temp dir.
8. On accept: move temp dir → `Translations\{id}\`, write `manifest.json`, register with `TranslationManager`, add to the switcher list.

## Switching + UI

New "Translations" section in Settings, alongside existing simple toggles:

- List: BSB (online) + each imported translation, showing display name and book coverage (e.g. "64/66 books"), with a selector for the active one.
- Per imported entry: rename, delete (removes folder + manifest + switcher entry, behind a confirm dialog — irreversible).
- "Import Translation ZIP…" button triggers the flow above.

Active translation id is persisted the same way as existing settings. `ScriptureViewModel` reads the active id and passes it through `BibleContentService.LoadBookAsync(bookCode, translationId)`, replacing today's implicit BSB-only call. If the active translation is missing the requested book, the view shows "not available in this translation" instead of blanking or crashing.

## What does NOT change

- `UsxBibleParser` parsing logic
- `books.json` / `last_verse.json` — canon and versification stay fixed; imported translations are assumed to match
- `UsxBibleApiLoader` / BSB's existing cache and prefetch behavior
- `UsxBibleAssetLoader` (embedded sample-verse loader)

## Security notes

- Zip-slip path traversal guard (step 4) is mandatory before any file write during extraction.
- Size cap (step 5) bounds memory/disk use from a malicious or corrupt ZIP.
- No network calls added — this is a pure local-file feature.
