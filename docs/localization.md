# UI translation

English is the default UI language, independent of the source and target languages of a dubbing project.

1. Copy `src/Dubbing.App/Languages/en.json` to a language code such as `fr.json`.
2. Translate values and retain keys, JSON escaping and placeholders such as `{0}`.
3. Put the file in the application's `Languages/` folder (source files there are copied during build).
4. Restart after adding a new file, select the language under Settings, and save.

TextCatalog loads English and overlays the selected file. Missing translations retain English. The Loc markup extension binds labels to observable localized values, so saving settings updates existing labels. View-model messages use the same catalog. The Spanish file demonstrates partial translation and English fallback.

Provider names, model names, paths, language codes and user-authored dialogue are data, not translated labels. Native provider diagnostic details are retained in English. For additional parameterized messages, add a stable key and call TextCatalog.Format; do not translate interpolation placeholders.
