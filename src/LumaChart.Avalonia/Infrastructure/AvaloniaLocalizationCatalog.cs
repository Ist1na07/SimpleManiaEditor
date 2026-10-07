using LumaChart.Core.Localization;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// The UI-facing face of <see cref="LocalizationCatalog"/>.
///
/// The catalog itself lives in LumaChart.Core so that every layer can translate
/// its own text; this type forwards to it and adds the few strings that are
/// about the language picker itself, which only the UI shows.
/// </summary>
public static class AvaloniaLocalizationCatalog
{
    /// <summary>The languages that ship a catalog file.</summary>
    public static IReadOnlyCollection<AvaloniaDisplayLanguage> TranslatedLanguages =>
        LocalizationCatalog.TranslatedLanguages;

    /// <summary>The loaded entries for one language, for coverage checks.</summary>
    public static IReadOnlyDictionary<string, string> Snapshot(
        AvaloniaDisplayLanguage language) => LocalizationCatalog.Snapshot(language);

    public static string Translate(string sourceText, AvaloniaDisplayLanguage language) =>
        LocalizationCatalog.Translate(sourceText, language);

    /// <inheritdoc cref="LocalizationCatalog.Text"/>
    public static string Text(string sourceText) => LocalizationCatalog.Text(sourceText);

    /// <inheritdoc cref="LocalizationCatalog.Format"/>
    public static string Format(string template, params object?[] arguments) =>
        LocalizationCatalog.Format(template, arguments);

    /// <inheritdoc cref="LocalizationCatalog.TryReformat"/>
    public static bool TryReformat(
        string formattedText,
        AvaloniaDisplayLanguage language,
        out string reformatted) =>
        LocalizationCatalog.TryReformat(formattedText, language, out reformatted);

    /// <inheritdoc cref="LocalizationCatalog.TryResolveSourceText"/>
    public static bool TryResolveSourceText(
        string localizedText,
        AvaloniaDisplayLanguage language,
        out string sourceText) =>
        LocalizationCatalog.TryResolveSourceText(localizedText, language, out sourceText);

    public static string CurrentLanguageStatus(
        AvaloniaLanguagePreference preference,
        AvaloniaDisplayLanguage language)
    {
        var name = NativeLanguageName(language);
        var followsSystem = preference == AvaloniaLanguagePreference.FollowSystem;
        return language switch
        {
            AvaloniaDisplayLanguage.SimplifiedChinese => followsSystem
                ? $"当前：{name}（跟随系统）"
                : $"当前：{name}",
            AvaloniaDisplayLanguage.TraditionalChinese => followsSystem
                ? $"目前：{name}（跟隨系統）"
                : $"目前：{name}",
            AvaloniaDisplayLanguage.Japanese => followsSystem
                ? $"現在：{name}（システムに従う）"
                : $"現在：{name}",
            AvaloniaDisplayLanguage.Korean => followsSystem
                ? $"현재: {name} (시스템)"
                : $"현재: {name}",
            AvaloniaDisplayLanguage.Spanish => followsSystem
                ? $"Actual: {name} (sistema)"
                : $"Actual: {name}",
            AvaloniaDisplayLanguage.French => followsSystem
                ? $"Actuel : {name} (système)"
                : $"Actuel : {name}",
            AvaloniaDisplayLanguage.German => followsSystem
                ? $"Aktuell: {name} (System)"
                : $"Aktuell: {name}",
            AvaloniaDisplayLanguage.PortugueseBrazil => followsSystem
                ? $"Atual: {name} (sistema)"
                : $"Atual: {name}",
            AvaloniaDisplayLanguage.Russian => followsSystem
                ? $"Текущий: {name} (система)"
                : $"Текущий: {name}",
            AvaloniaDisplayLanguage.Italian => followsSystem
                ? $"Attuale: {name} (sistema)"
                : $"Attuale: {name}",
            AvaloniaDisplayLanguage.Dutch => followsSystem
                ? $"Huidig: {name} (systeem)"
                : $"Huidig: {name}",
            AvaloniaDisplayLanguage.Polish => followsSystem
                ? $"Bieżący: {name} (system)"
                : $"Bieżący: {name}",
            AvaloniaDisplayLanguage.Turkish => followsSystem
                ? $"Geçerli: {name} (sistem)"
                : $"Geçerli: {name}",
            AvaloniaDisplayLanguage.Vietnamese => followsSystem
                ? $"Hiện tại: {name} (hệ thống)"
                : $"Hiện tại: {name}",
            AvaloniaDisplayLanguage.Arabic => followsSystem
                ? $"الحالي: {name} (النظام)"
                : $"الحالي: {name}",
            AvaloniaDisplayLanguage.Hindi => followsSystem
                ? $"वर्तमान: {name} (सिस्टम)"
                : $"वर्तमान: {name}",
            AvaloniaDisplayLanguage.Thai => followsSystem
                ? $"ปัจจุบัน: {name} (ระบบ)"
                : $"ปัจจุบัน: {name}",
            AvaloniaDisplayLanguage.Indonesian => followsSystem
                ? $"Saat ini: {name} (sistem)"
                : $"Saat ini: {name}",
            _ => followsSystem
                ? $"Current: {name} (system)"
                : $"Current: {name}"
        };
    }

    public static string LanguageUpdatedText(AvaloniaDisplayLanguage language) => language switch
    {
        AvaloniaDisplayLanguage.SimplifiedChinese => "界面语言已更新。",
        AvaloniaDisplayLanguage.TraditionalChinese => "介面語言已更新。",
        AvaloniaDisplayLanguage.Japanese => "表示言語を更新しました。",
        AvaloniaDisplayLanguage.Korean => "표시 언어를 업데이트했습니다.",
        AvaloniaDisplayLanguage.Spanish => "Idioma de visualización actualizado.",
        AvaloniaDisplayLanguage.French => "Langue d'affichage mise à jour.",
        AvaloniaDisplayLanguage.German => "Anzeigesprache aktualisiert.",
        AvaloniaDisplayLanguage.PortugueseBrazil => "Idioma de exibição atualizado.",
        AvaloniaDisplayLanguage.Russian => "Язык интерфейса обновлён.",
        AvaloniaDisplayLanguage.Italian => "Lingua dell'interfaccia aggiornata.",
        AvaloniaDisplayLanguage.Dutch => "Weergavetaal bijgewerkt.",
        AvaloniaDisplayLanguage.Polish => "Język interfejsu zaktualizowany.",
        AvaloniaDisplayLanguage.Turkish => "Görüntüleme dili güncellendi.",
        AvaloniaDisplayLanguage.Vietnamese => "Đã cập nhật ngôn ngữ hiển thị.",
        AvaloniaDisplayLanguage.Arabic => "تم تحديث لغة العرض.",
        AvaloniaDisplayLanguage.Hindi => "प्रदर्शन भाषा अपडेट की गई।",
        AvaloniaDisplayLanguage.Thai => "อัปเดตภาษาที่แสดงแล้ว",
        AvaloniaDisplayLanguage.Indonesian => "Bahasa tampilan diperbarui.",
        _ => "Display language updated."
    };

    public static string LanguageSaveFailedText(
        AvaloniaDisplayLanguage language,
        string message) => language switch
    {
        AvaloniaDisplayLanguage.SimplifiedChinese => $"界面语言已切换，但保存失败：{message}",
        AvaloniaDisplayLanguage.TraditionalChinese => $"介面語言已切換，但儲存失敗：{message}",
        AvaloniaDisplayLanguage.Japanese => $"表示言語は変更されましたが、保存できませんでした: {message}",
        AvaloniaDisplayLanguage.Korean => $"표시 언어는 변경되었지만 저장하지 못했습니다: {message}",
        AvaloniaDisplayLanguage.Spanish => $"El idioma cambió, pero no se pudo guardar: {message}",
        AvaloniaDisplayLanguage.French => $"La langue a changé, mais l'enregistrement a échoué : {message}",
        AvaloniaDisplayLanguage.German => $"Die Sprache wurde geändert, konnte aber nicht gespeichert werden: {message}",
        AvaloniaDisplayLanguage.PortugueseBrazil => $"O idioma foi alterado, mas não pôde ser salvo: {message}",
        AvaloniaDisplayLanguage.Russian => $"Язык изменён, но сохранить настройку не удалось: {message}",
        AvaloniaDisplayLanguage.Italian => $"La lingua è cambiata, ma non è stato possibile salvarla: {message}",
        AvaloniaDisplayLanguage.Dutch => $"De taal is gewijzigd, maar kon niet worden opgeslagen: {message}",
        AvaloniaDisplayLanguage.Polish => $"Język zmieniono, ale nie udało się go zapisać: {message}",
        AvaloniaDisplayLanguage.Turkish => $"Dil değiştirildi ancak kaydedilemedi: {message}",
        AvaloniaDisplayLanguage.Vietnamese => $"Đã đổi ngôn ngữ nhưng không thể lưu: {message}",
        AvaloniaDisplayLanguage.Arabic => $"تم تغيير اللغة، لكن تعذر حفظها: {message}",
        AvaloniaDisplayLanguage.Hindi => $"भाषा बदल दी गई, लेकिन सहेजी नहीं जा सकी: {message}",
        AvaloniaDisplayLanguage.Thai => $"เปลี่ยนภาษาแล้ว แต่ไม่สามารถบันทึกได้: {message}",
        AvaloniaDisplayLanguage.Indonesian => $"Bahasa diubah, tetapi tidak dapat disimpan: {message}",
        _ => $"Language changed, but it could not be saved: {message}"
    };

    private static string NativeLanguageName(AvaloniaDisplayLanguage language) => language switch
    {
        AvaloniaDisplayLanguage.SimplifiedChinese => "简体中文",
        AvaloniaDisplayLanguage.TraditionalChinese => "繁體中文",
        AvaloniaDisplayLanguage.Japanese => "日本語",
        AvaloniaDisplayLanguage.Korean => "한국어",
        AvaloniaDisplayLanguage.Spanish => "Español",
        AvaloniaDisplayLanguage.French => "Français",
        AvaloniaDisplayLanguage.German => "Deutsch",
        AvaloniaDisplayLanguage.PortugueseBrazil => "Português (Brasil)",
        AvaloniaDisplayLanguage.Russian => "Русский",
        AvaloniaDisplayLanguage.Italian => "Italiano",
        AvaloniaDisplayLanguage.Dutch => "Nederlands",
        AvaloniaDisplayLanguage.Polish => "Polski",
        AvaloniaDisplayLanguage.Turkish => "Türkçe",
        AvaloniaDisplayLanguage.Vietnamese => "Tiếng Việt",
        AvaloniaDisplayLanguage.Arabic => "العربية",
        AvaloniaDisplayLanguage.Hindi => "हिन्दी",
        AvaloniaDisplayLanguage.Thai => "ไทย",
        AvaloniaDisplayLanguage.Indonesian => "Bahasa Indonesia",
        _ => "English"
    };
}
