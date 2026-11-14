# ReplayKit localization

`shared/LanguageMatch.cs` owns the supported language list and Windows language matching. ReplayKit follows the language OBS is using when supported, with English as the fallback. Translation files ship with the installer. There is no translation service at runtime.

## Add or edit a message

1. Define a feature-scoped ID such as `clips.showInClips` in `messages/en-US.json`. Give it English `text` and a `context` that explains where it appears. Keep the ID when rewording the same message. Use a new ID for a different meaning, even when the English is identical.
2. Run `python -B ReplayKit/tools/i18n/messages.py` from the repository root. It prints the messages that need translation or review and the source revision to put in each locale's `source` field. Keep named placeholders intact. Translate the whole sentence, including the words around the placeholders. For an existing translation, update its source revision after reviewing the new English.
3. Run the checker again and build. Release packaging runs the checker and generates the runtime catalogs. Python 3.10 or newer is required at build time. Keep translator context and revisions in the source files.

If a message has exactly the same meaning and English text as an OBS frontend message, add its OBS key as `obsKey` in the English catalog. To fill missing values from OBS, run `python -B ReplayKit/tools/i18n/messages.py --seed-obs "C:/Program Files/obs-studio/data/obs-studio/locale"`. The command leaves existing translations intact. Review seeded text in context.

## Use a message

- JavaScript uses `RK_M("clips.showInClips")` or `RK_M("clips.selectedCount", { count })`. For a plural message, pass a number and provide all plural forms required by each language. The browser selects a form and formats the number for the active locale.
- Static HTML uses `data-i18n="common.close"` on a text-only element or `data-i18n-aria-label="common.close"` on a control. The same pattern works for `title`, `placeholder`, `data-tooltip`, and `alt`. Keep icons outside the text-only element. For newly created controls, render the message as you create the element.
- The helper uses `Locales.Message("picker.importAudio")`. The installer uses `SetupLocale.Message("setup.windowTitle")`. Native OBS code uses `TrMessage("capture.clipSaved", {{"length", length}})`. Pass raw values for placeholders and escape at the output boundary.
- Use text properties such as `textContent` for translated messages. Keep filenames, game names, account data, paths, setting values, and OBS identifiers out of translation. On older pages, mark user content with `translate="no"` so the legacy observer leaves it alone.

The existing English-keyed tables in `resources/locale` support screens that are still migrating. Keep existing entries while callers use them. When every message on a page has been migrated, add `data-i18n-explicit` to its `html` element. That page then loads only the keyed messages and skips the legacy text observer. The notification reader is an example. Run `python -B ReplayKit/tools/i18n/i18n.py check` when editing the legacy tables.

Check layout with a longer translation such as German and a CJK language. Form controls inherit the selected interface font. Explicit font previews and text the editor draws into video use their own font.
