// The display-language enums moved down to LumaChart.Core, because text that
// needs translating is not confined to the UI project: the library card
// subtitle is built in LumaChart.Application, and account and analysis messages
// come from LumaChart.Online and LumaChart.Core. Those projects cannot
// reference the Avalonia UI, so the catalog and its enums live below them.
//
// These aliases keep the names the UI has always used, so the move required no
// edits at any of the call sites.

global using AvaloniaDisplayLanguage = LumaChart.Core.Localization.LumaChartDisplayLanguage;
global using AvaloniaLanguagePreference = LumaChart.Core.Localization.LumaChartLanguagePreference;
