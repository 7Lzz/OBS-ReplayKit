# ReplayKit localization

`shared/LanguageMatch.cs` owns the supported language list and Windows language matching. ReplayKit follows the language OBS is using when supported, with English as the fallback. Translation files ship with the installer. There is no translation service at runtime.

## Add or edit a message

1. Define a feature-scoped ID such as `clips.showInClips` in `messages/en-US.json`. Give it English `text` and a `context` that explains where it appears. Keep the ID when rewording the same message. Use a new ID for a different meaning, even when the English is identical.
2. Run `python -B ReplayKit/tools/i18n/messages.py` from the repository root. It prints the messages that need translation or review and the source revision to put in each locale's `source` field. Keep named placeholders intact. Translate the whole sentence, including the words around the placeholders. For an existing translation, update its source revision after reviewing the new English.
3. Run the checker again and build. Release packaging runs the checker and generates the runtime catalogs. Python 3.10 or newer is required at build time. Keep translator context and revisions in the source files.

If a message has exactly the same meaning and English text as an OBS frontend message, add its OBS key as `obsKey` in the English catalog. To fill missing values from OBS, run `python -B ReplayKit/tools/i18n/messages.py --seed-obs "C:/Program Files/obs-studio/data/obs-studio/locale"`. The command leaves existing translations intact. Review seeded text in context.

## Use a message

- JavaScript uses `RK_M("clips.showInClips")` or `RK_M("clips.selectedCount", { count })`. For a plural message, pass a number and provide all plural forms required by each language. The browser selects a form and formats the number for the active locale.
- Text that needs bold words goes through `RK_MH("storage.usedOfLimit", { used, limit })`, which returns HTML: the message marks its bold words with `**`, the message text is escaped, and the values go in as given, so escape any value that is not markup. A message never holds `<` or `>`.
- HTML uses `data-i18n="common.close"` on a text-only element or `data-i18n-aria-label="common.close"` on a control. The same pattern works for `title`, `placeholder`, `data-tooltip`, and `alt`. Keep icons outside the text-only element. A bound element renders the moment the parser or a script adds it, so markup a script builds can carry the same attributes, and setting `data-i18n` on an element switches it to another message.
- The helper uses `Locales.Message("picker.importAudio")`. The installer uses `SetupLocale.Message("setup.windowTitle")`. Native OBS code uses `TrMessage("capture.clipSaved", {{"length", length}})`. Pass raw values for placeholders and escape at the output boundary.
- Use text properties such as `textContent` for translated messages. Keep filenames, game names, account data, paths, setting values, and OBS identifiers out of translation. Data that arrives in English, such as folder names and audio track titles, stays as it is, and the page shows the message for what it stands for.

The checker fails when an English-keyed lookup (`RK_T`, `Locales.T`, `SetupLocale.T` or `Tr`) comes back; every text has a message ID.

Check layout with a longer translation such as German and a CJK language. Form controls inherit the selected interface font. Explicit font previews and text the editor draws into video use their own font.
