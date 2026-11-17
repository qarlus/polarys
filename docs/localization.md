# Localization

Polarys stores the selected UI language as a BCP 47 culture tag, such as `tr-TR`, `en-US`, or `ja-JP`. The language catalog reads embedded JSON files from `Services/Localization/Languages`. The file name is the culture tag and each JSON property is a localization key.

On first launch, the app selects a translation matching the Windows UI language. Regional variants keep their culture-specific date and number formatting while using the matching language translation. If the Windows UI language is not available, the app starts in English. A missing translation in another language uses the English entry; a missing English key raises an explicit error.

## Add a language

1. Copy `en-US.json` to a file named with the new culture tag, such as `fr-FR.json`.
2. Translate the values while keeping every symbolic key and any format placeholders such as `{0}` unchanged. Keys are stable identifiers and must not contain translated text.
3. The project embeds JSON files in this folder automatically. The language picker uses the culture's native name, so no language enum, switch statement, or UI control needs editing.
4. Keep error and status results as localization keys in services and resolve them in the UI. Translate launcher labels, errors, and status messages too. If a key is not ready yet, English is shown until its translation is added.

## Existing fixed text

User-facing labels and errors belong in the locale JSON files. Product names, keyboard shortcuts, file formats, protocol values, operating-system identifiers, and source data such as city names are not translated UI copy.
