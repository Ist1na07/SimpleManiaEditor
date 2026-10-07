using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

public static class WindowsLocalizationSpecs
{
    public static IReadOnlyList<WindowsLocalizationSpecResult> Run(string isolationRoot)
    {
        var results = new List<WindowsLocalizationSpecResult>();
        Check(results, "avalonia-language-resolution-follows-system-and-explicit-choice-wins",
            VerifyLanguageResolution);
        Check(results, "avalonia-language-settings-round-trip-and-fail-closed",
            () => VerifySettingsStore(isolationRoot));
        Check(results, "avalonia-english-catalog-keeps-unknown-source-text",
            VerifyCatalogFallback);
        Check(results, "avalonia-runtime-localization-preserves-source-and-late-content",
            VerifyRuntimeLocalization);
        Check(results, "avalonia-main-window-wires-language-and-materialized-content-refresh",
            VerifyMainWindowWiring);
        Check(results, "avalonia-mobile-font-fallback-covers-traditional-japanese-and-korean",
            VerifyMobileCjkFallback);
        Check(results, "avalonia-display-language-coverage-does-not-regress",
            VerifyCatalogCoverage);
        return results;
    }

    private static string VerifyCatalogCoverage()
    {
        var uiRoot = Path.GetDirectoryName(FindProjectFile("LumaChart.Avalonia", "MainWindow.cs"))
            ?? throw new DirectoryNotFoundException("Could not locate the Avalonia UI source root.");
        var repositoryRoot = Path.GetDirectoryName(uiRoot)
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
        var resourceRoot = Path.Combine(repositoryRoot, "LumaChart.Core", "Resources", "i18n");
        return LocalizationCoverageGate.Verify(repositoryRoot, resourceRoot);
    }

    private static string VerifyLanguageResolution()
    {
        var systemCases = new (string Culture, AvaloniaDisplayLanguage Expected)[]
        {
            ("zh-CN", AvaloniaDisplayLanguage.SimplifiedChinese),
            ("zh-TW", AvaloniaDisplayLanguage.TraditionalChinese),
            ("zh-HK", AvaloniaDisplayLanguage.TraditionalChinese),
            ("ja-JP", AvaloniaDisplayLanguage.Japanese),
            ("ko-KR", AvaloniaDisplayLanguage.Korean),
            ("es-ES", AvaloniaDisplayLanguage.Spanish),
            ("fr-FR", AvaloniaDisplayLanguage.French),
            ("de-DE", AvaloniaDisplayLanguage.German),
            ("pt-BR", AvaloniaDisplayLanguage.PortugueseBrazil),
            ("ru-RU", AvaloniaDisplayLanguage.Russian),
            ("it-IT", AvaloniaDisplayLanguage.Italian),
            ("nl-NL", AvaloniaDisplayLanguage.Dutch),
            ("pl-PL", AvaloniaDisplayLanguage.Polish),
            ("tr-TR", AvaloniaDisplayLanguage.Turkish),
            ("vi-VN", AvaloniaDisplayLanguage.Vietnamese),
            ("ar-SA", AvaloniaDisplayLanguage.Arabic),
            ("hi-IN", AvaloniaDisplayLanguage.Hindi),
            ("th-TH", AvaloniaDisplayLanguage.Thai),
            ("id-ID", AvaloniaDisplayLanguage.Indonesian),
            ("en-US", AvaloniaDisplayLanguage.English),
            ("bn-BD", AvaloniaDisplayLanguage.English)
        };
        Require(systemCases.All(item =>
                AvaloniaLanguageSettings.ResolveEffectiveLanguage(
                    AvaloniaLanguagePreference.FollowSystem,
                    CultureInfo.GetCultureInfo(item.Culture)) == item.Expected),
            "Follow system did not resolve the supported cultures deterministically.");
        Require(
            AvaloniaLanguageSettings.ResolveEffectiveLanguage(
                AvaloniaLanguagePreference.Japanese,
                CultureInfo.GetCultureInfo("zh-CN")) == AvaloniaDisplayLanguage.Japanese &&
            AvaloniaLanguageSettings.ResolveEffectiveLanguage(
                AvaloniaLanguagePreference.Korean,
                CultureInfo.GetCultureInfo("en-US")) == AvaloniaDisplayLanguage.Korean &&
            AvaloniaLanguageSettings.ResolveEffectiveLanguage(
                AvaloniaLanguagePreference.Vietnamese,
                CultureInfo.GetCultureInfo("en-US")) == AvaloniaDisplayLanguage.Vietnamese &&
            AvaloniaLanguageSettings.ResolveEffectiveLanguage(
                AvaloniaLanguagePreference.Arabic,
                CultureInfo.GetCultureInfo("en-US")) == AvaloniaDisplayLanguage.Arabic &&
            AvaloniaLanguageSettings.ResolveEffectiveLanguage(
                AvaloniaLanguagePreference.Hindi,
                CultureInfo.GetCultureInfo("en-US")) == AvaloniaDisplayLanguage.Hindi,
            "An explicit language choice did not override the system culture.");
        Require(
            new AvaloniaLanguageSettings((AvaloniaLanguagePreference)999).Preference ==
            AvaloniaLanguagePreference.FollowSystem,
            "An invalid in-memory language preference did not fail closed.");
        return $"systemCultures={systemCases.Length}; traditional=zh-TW/zh-HK; " +
               "fallback=English; explicitChoice=authoritative";
    }

    private static string VerifySettingsStore(string isolationRoot)
    {
        const string variable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(isolationRoot, "localization-settings");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(variable, root);
            var missing = AvaloniaLanguageSettingsStore.Load();
            Require(
                missing.Preference == AvaloniaLanguagePreference.FollowSystem,
                "A missing language file did not default to Follow system.");

            Require(
                AvaloniaLanguageSettingsStore.Save(
                    new AvaloniaLanguageSettings(AvaloniaLanguagePreference.Korean)),
                "A language preference save was refused with no unreadable file present.");
            var loaded = AvaloniaLanguageSettingsStore.Load();
            using var persisted = JsonDocument.Parse(
                File.ReadAllBytes(AvaloniaLanguageSettingsStore.SettingsPath));
            var temporaryCount = Directory
                .EnumerateFiles(root, ".language-settings.json.*.tmp")
                .Count();
            Require(
                loaded.Preference == AvaloniaLanguagePreference.Korean &&
                persisted.RootElement.GetProperty("SchemaVersion").GetInt32() ==
                AvaloniaLanguageSettingsStore.CurrentSchemaVersion &&
                persisted.RootElement.GetProperty("Preference").GetInt32() ==
                (int)AvaloniaLanguagePreference.Korean &&
                temporaryCount == 0,
                "The language preference did not round-trip atomically.");

            File.WriteAllText(AvaloniaLanguageSettingsStore.SettingsPath, "{not-json");
            Require(
                AvaloniaLanguageSettingsStore.Load().Preference ==
                AvaloniaLanguagePreference.FollowSystem,
                "A corrupt language file did not fail closed.");
            // The unreadable file is moved aside rather than left for the next
            // save to overwrite with the default.
            Require(
                Directory.EnumerateFiles(root, "language-settings.json.corrupt-*").Any(),
                "A corrupt language file was not preserved beside the settings.");
            File.WriteAllText(
                AvaloniaLanguageSettingsStore.SettingsPath,
                "{\"SchemaVersion\":999,\"Preference\":2}");
            Require(
                AvaloniaLanguageSettingsStore.Load().Preference ==
                AvaloniaLanguagePreference.FollowSystem,
                "An unknown language schema was partially trusted.");
            File.WriteAllText(
                AvaloniaLanguageSettingsStore.SettingsPath,
                "{\"SchemaVersion\":1,\"Preference\":999}");
            Require(
                AvaloniaLanguageSettingsStore.Load().Preference ==
                AvaloniaLanguagePreference.FollowSystem,
                "An invalid persisted language value did not fail closed.");
            return $"path={AvaloniaLanguageSettingsStore.SettingsPath}; schema=1; temp=0";
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static string VerifyCatalogFallback()
    {
        var expectedSettings = new Dictionary<AvaloniaDisplayLanguage, string>
        {
            [AvaloniaDisplayLanguage.TraditionalChinese] = "設定",
            [AvaloniaDisplayLanguage.English] = "Settings",
            [AvaloniaDisplayLanguage.Japanese] = "設定",
            [AvaloniaDisplayLanguage.Korean] = "설정",
            [AvaloniaDisplayLanguage.Spanish] = "Configuración",
            [AvaloniaDisplayLanguage.French] = "Paramètres",
            [AvaloniaDisplayLanguage.German] = "Einstellungen",
            [AvaloniaDisplayLanguage.PortugueseBrazil] = "Configurações",
            [AvaloniaDisplayLanguage.Russian] = "Настройки",
            [AvaloniaDisplayLanguage.Italian] = "Impostazioni",
            [AvaloniaDisplayLanguage.Dutch] = "Instellingen",
            [AvaloniaDisplayLanguage.Polish] = "Ustawienia",
            [AvaloniaDisplayLanguage.Turkish] = "Ayarlar",
            [AvaloniaDisplayLanguage.Vietnamese] = "Cài đặt",
            [AvaloniaDisplayLanguage.Arabic] = "الإعدادات",
            [AvaloniaDisplayLanguage.Hindi] = "सेटिंग्स",
            [AvaloniaDisplayLanguage.Thai] = "การตั้งค่า",
            [AvaloniaDisplayLanguage.Indonesian] = "Pengaturan"
        };
        var unknown = AvaloniaLocalizationCatalog.Translate(
            "尚未进入英文目录的动态文案",
            AvaloniaDisplayLanguage.Japanese);
        // A label the English catalog carries but Korean does not, so it
        // exercises the secondary fallback. Korean now covers every label the
        // UI shows, so this has to be one of the entries that survive for
        // text the UI composes rather than displays verbatim.
        var englishFallback = AvaloniaLocalizationCatalog.Translate(
            "刷新音频设备",
            AvaloniaDisplayLanguage.Korean);
        var chinese = AvaloniaLocalizationCatalog.Translate(
            "保存",
            AvaloniaDisplayLanguage.SimplifiedChinese);
        Require(expectedSettings.All(item =>
                AvaloniaLocalizationCatalog.Translate("设置", item.Key) == item.Value),
            "A major language is missing the required Settings translation.");
        Require(unknown == "尚未进入英文目录的动态文案" && unknown.Length > 0,
            "An untranslated catalog lookup dropped its source text.");
        Require(englishFallback == "Refresh audio devices",
            "A major-language catalog did not use the English secondary fallback.");
        Require(chinese == "保存",
            "Simplified Chinese did not preserve the source label.");
        Require(
            AvaloniaLocalizationCatalog.Translate("▶ 播放", AvaloniaDisplayLanguage.Japanese) ==
                "▶ 再生" &&
            AvaloniaLocalizationCatalog.Translate("Ⅱ 暂停", AvaloniaDisplayLanguage.Korean) ==
                "Ⅱ 일시 정지" &&
            AvaloniaLocalizationCatalog.Translate("好友", AvaloniaDisplayLanguage.Japanese) ==
                "フレンド" &&
            AvaloniaLocalizationCatalog.Translate("联机", AvaloniaDisplayLanguage.TraditionalChinese) ==
                "聯機" &&
            AvaloniaLocalizationCatalog.Translate("制作者", AvaloniaDisplayLanguage.Spanish) ==
                "Creador" &&
            AvaloniaLocalizationCatalog.Translate(
                "好友 Alice · 在线",
                AvaloniaDisplayLanguage.English) == "好友 Alice · 在线" &&
            AvaloniaLocalizationCatalog.Translate(
                "BPM 128 · Offset 12 ms",
                AvaloniaDisplayLanguage.English) == "BPM 128 · Offset 12 ms" &&
            // A label an interpolated string built at run time. It is a known
            // gap: until the call site becomes a format template, it stays in
            // the source language rather than being stitched together word by
            // word into "3 个文件夹 · 2 charts".
            AvaloniaLocalizationCatalog.Translate(
                "3 个文件夹 · 2 张谱面",
                AvaloniaDisplayLanguage.English) == "3 个文件夹 · 2 张谱面",
            "A composed label that is not a catalog key must be returned whole. " +
            "Stitching it together from recognised fragments is what produced " +
            "labels mixing three languages in one line.");
        Require(
            AvaloniaLocalizationCatalog.Translate("我的主页", AvaloniaDisplayLanguage.Korean) ==
                "내 프로필" &&
            AvaloniaLocalizationCatalog.Translate("浏览在线社区、搜索玩家并打开公开资料。", AvaloniaDisplayLanguage.Korean) ==
                "온라인 커뮤니티를 둘러보고 플레이어를 검색하여 공개 프로필을 엽니다." &&
            AvaloniaLocalizationCatalog.Translate("搜索玩家", AvaloniaDisplayLanguage.Korean) ==
                "플레이어 검색" &&
            AvaloniaLocalizationCatalog.Translate("查看资料", AvaloniaDisplayLanguage.Korean) ==
                "프로필 보기" &&
            AvaloniaLocalizationCatalog.Translate("返回编辑器", AvaloniaDisplayLanguage.Korean) ==
                "편집기로 돌아가기" &&
            AvaloniaLocalizationCatalog.Translate("Group Manager", AvaloniaDisplayLanguage.Korean) ==
                "그룹 관리자" &&
            AvaloniaLocalizationCatalog.Translate("新建并使用", AvaloniaDisplayLanguage.Korean) ==
                "만들고 사용" &&
            AvaloniaLocalizationCatalog.Translate("设为当前放置组", AvaloniaDisplayLanguage.Korean) ==
                "현재 배치 그룹으로 설정" &&
            AvaloniaLocalizationCatalog.Translate("将该组分配给选中 Note", AvaloniaDisplayLanguage.Korean) ==
                "선택한 Note에 이 그룹 할당" &&
            AvaloniaLocalizationCatalog.Translate("全部显示", AvaloniaDisplayLanguage.Korean) ==
                "모두 표시" &&
            AvaloniaLocalizationCatalog.Translate("隐藏非零组", AvaloniaDisplayLanguage.Korean) ==
                "0이 아닌 그룹 숨기기" &&
            AvaloniaLocalizationCatalog.Translate("清除分组", AvaloniaDisplayLanguage.Korean) ==
                "그룹 지우기",
            "Korean community and group-manager labels were not translated as complete source strings.");
        Require(
            AvaloniaLocalizationCatalog.Translate("新建", AvaloniaDisplayLanguage.English) ==
                "New" &&
            AvaloniaLocalizationCatalog.Translate("打开", AvaloniaDisplayLanguage.English) ==
                "Open" &&
            AvaloniaLocalizationCatalog.Translate("备份", AvaloniaDisplayLanguage.English) ==
                "Backup" &&
            AvaloniaLocalizationCatalog.Translate("恢复", AvaloniaDisplayLanguage.English) ==
                "Restore" &&
            AvaloniaLocalizationCatalog.Translate("检查", AvaloniaDisplayLanguage.English) ==
                "Check" &&
            AvaloniaLocalizationCatalog.Translate("从此处", AvaloniaDisplayLanguage.English) ==
                "From here" &&
            AvaloniaLocalizationCatalog.Translate("同步 osu! 头像", AvaloniaDisplayLanguage.English) ==
                "Sync osu! avatar" &&
            AvaloniaLocalizationCatalog.Translate("立即刷新", AvaloniaDisplayLanguage.English) ==
                "Refresh now" &&
            AvaloniaLocalizationCatalog.Translate("刷新索引", AvaloniaDisplayLanguage.English) ==
                "Refresh index" &&
            AvaloniaLocalizationCatalog.Translate("· 已保存", AvaloniaDisplayLanguage.English) ==
                "· Saved" &&
            AvaloniaLocalizationCatalog.Translate("无", AvaloniaDisplayLanguage.English) ==
                "None" &&
            AvaloniaLocalizationCatalog.Translate("12 列", AvaloniaDisplayLanguage.English) ==
                "12 columns" &&
            AvaloniaLocalizationCatalog.Translate("查看新的关注提醒。", AvaloniaDisplayLanguage.English) ==
                "View new follow notifications." &&
            AvaloniaLocalizationCatalog.Translate("首页按歌曲文件夹归组；单击歌曲卡片即可展开或收起谱面列表。支持 Malody 二级目录与谱包。工作目录可选择游戏谱面存放路径。", AvaloniaDisplayLanguage.English) ==
                "The home page groups charts by song folder. Click a song card to expand or collapse its chart list. Malody subfolders and chart packages are supported. The workspace can point to your game chart directory." &&
            AvaloniaLocalizationCatalog.Translate("范围按 Best ≤ Great ≤ Good ≤ Miss 自动约束", AvaloniaDisplayLanguage.English) ==
                "The ranges are automatically constrained as Best ≤ Great ≤ Good ≤ Miss",
            "English editor, library, notification, and explanatory copy still contained untranslated source text.");
        Require(
            AvaloniaLocalizationCatalog.Translate("Key 音", AvaloniaDisplayLanguage.English) ==
                "Key sound" &&
            AvaloniaLocalizationCatalog.Translate("回到开头", AvaloniaDisplayLanguage.English) ==
                "Go to beginning" &&
            AvaloniaLocalizationCatalog.Translate("单谱文件（不包含资源）", AvaloniaDisplayLanguage.English) ==
                "Single chart file (resources not included)" &&
            AvaloniaLocalizationCatalog.Translate("整谱压缩包", AvaloniaDisplayLanguage.English) ==
                "Full-chart package" &&
            AvaloniaLocalizationCatalog.Translate("单一倍速压缩包", AvaloniaDisplayLanguage.English) ==
                "Single-rate package" &&
            AvaloniaLocalizationCatalog.Translate("范围倍速包", AvaloniaDisplayLanguage.English) ==
                "Rate-range package" &&
            AvaloniaLocalizationCatalog.Translate("Key 音", AvaloniaDisplayLanguage.Korean) ==
                "키음" &&
            AvaloniaLocalizationCatalog.Translate("回到开头", AvaloniaDisplayLanguage.Korean) ==
                "처음으로 이동" &&
            AvaloniaLocalizationCatalog.Translate("范围倍速包", AvaloniaDisplayLanguage.Japanese) ==
                "速度範囲パッケージ" &&
            AvaloniaLocalizationCatalog.Translate("整谱压缩包", AvaloniaDisplayLanguage.Italian) ==
                "Pacchetto schema completo" &&
            AvaloniaLocalizationCatalog.Translate("单谱文件（不包含资源）", AvaloniaDisplayLanguage.Arabic) ==
                "ملف مخطط واحد (من دون موارد)",
            "Key-sound, go-to-beginning, or export-mode labels were missing from a supported language catalog.");
        return $"languages={expectedSettings.Count}; englishFallback={englishFallback}; " +
               $"unknownPreserved={unknown.Length}; chinese={chinese}";
    }

    private static string VerifyRuntimeLocalization()
    {
        var title = new TextBlock { Text = "设置" };
        var save = new Button { Content = "保存" };
        var input = new TextBox { PlaceholderText = "选择文件" };
        ToolTip.SetTip(save, "选择界面显示语言");
        var root = new StackPanel { Children = { title, save, input } };
        var controller = new AvaloniaLocalizationController(
            root,
            AvaloniaLanguagePreference.Japanese);

        controller.Apply();
        Require(
            title.Text == "設定" &&
            Equals(save.Content, "保存") &&
            input.PlaceholderText == "ファイルを選択" &&
            Equals(ToolTip.GetTip(save), "表示言語を選択"),
            "The Japanese controller did not cover common control text properties.");

        save.Content = "取消";
        controller.Apply();
        Require(Equals(save.Content, "キャンセル"),
            "A dynamic source-text update was overwritten by an older tracked label.");

        controller.SetPreference(AvaloniaLanguagePreference.Korean);
        Require(
            title.Text == "설정" &&
            Equals(save.Content, "취소") &&
            input.PlaceholderText == "파일 선택",
            "Switching between Japanese and Korean did not reapply retained source text.");

        controller.SetPreference(AvaloniaLanguagePreference.Italian);
        Require(
            title.Text == "Impostazioni" &&
            Equals(save.Content, "Annulla") &&
            input.PlaceholderText == "Scegli file",
            "Switching to Italian did not reapply retained source text.");

        controller.SetPreference(AvaloniaLanguagePreference.Arabic);
        Require(
            title.Text == "الإعدادات" &&
            Equals(save.Content, "إلغاء") &&
            input.PlaceholderText == "اختيار ملف",
            "Switching to Arabic did not reapply retained source text.");

        var lateTitle = new TextBlock { Text = "语言与地区" };
        controller.Apply(lateTitle);
        Require(lateTitle.Text == "اللغة والمنطقة",
            "A later-materialized control was not localized.");

        var unknown = new TextBlock { Text = "动态未翻译文案" };
        root.Children.Add(unknown);
        controller.Apply();
        Require(unknown.Text == "动态未翻译文案" && unknown.Text.Length > 0,
            "An unknown runtime label became blank or changed.");

        controller.SetPreference(AvaloniaLanguagePreference.SimplifiedChinese);
        Require(
            title.Text == "设置" &&
            Equals(save.Content, "取消") &&
            input.PlaceholderText == "选择文件" &&
            lateTitle.Text == "语言与地区" &&
            unknown.Text == "动态未翻译文案",
            "Switching back to Chinese did not restore every tracked source value.");
        Require(
            WindowsInspectorParityPolicy.ResolveSettingsTabIndex("Settings.Language") == 0,
            "The language selector is missing from the Settings keyboard focus order.");

        // "Open" is the English text of both 打开 and 待处理. A remembered source
        // must survive a switch, and a label written already translated must
        // never be read back as a guessed source.
        var status = new TextBlock { Text = "设置" };
        var written = new TextBlock { Text = "设置" };
        var statusRoot = new StackPanel { Children = { status, written } };
        var statusController = new AvaloniaLocalizationController(
            statusRoot,
            AvaloniaLanguagePreference.English);
        statusController.Apply();
        status.Text = "待处理";
        written.Text = "Open";
        statusController.Apply();
        statusController.SetPreference(AvaloniaLanguagePreference.Japanese);
        Require(status.Text == AvaloniaLocalizationCatalog.Translate(
                "待处理",
                AvaloniaDisplayLanguage.Japanese),
            "A remembered source text was lost across a language switch.");
        Require(written.Text == "Open",
            "An ambiguous translated label was mapped back to a guessed source text.");
        Require(ReferenceEquals(AvaloniaLocalizationRuntime.Attach(statusRoot), statusController),
            "A second localization controller was attached to a root that already had one.");
        return "japanese=true; korean=true; italian=true; arabic=true; dynamic=true; " +
               "lateContent=true; fallback=true; restored=true; ambiguousReverse=false; " +
               "singleController=true";
    }

    private static string VerifyMainWindowWiring()
    {
        var source = File.ReadAllText(FindProjectFile(
            "LumaChart.Avalonia",
            "MainWindow.cs"));
        var localizationSource = File.ReadAllText(FindProjectFile(
            "LumaChart.Avalonia",
            "Infrastructure",
            "AvaloniaLocalizationController.cs"));
        Require(
            source.Contains("BuildLanguageSettings()", StringComparison.Ordinal) &&
            source.Contains(
                "AutomationProperties.SetAutomationId(languagePreferenceBox, \"Settings.Language\")",
                StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.TraditionalChinese", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Japanese", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Korean", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Russian", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Italian", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Dutch", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Polish", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Turkish", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Vietnamese", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Arabic", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Hindi", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Thai", StringComparison.Ordinal) &&
            source.Contains("AvaloniaLanguagePreference.Indonesian", StringComparison.Ordinal) &&
            source.Contains("RefreshFollowSystemLanguage();", StringComparison.Ordinal) &&
            source.Contains("localizationController.ScheduleApply();", StringComparison.Ordinal) &&
            source.Contains(
                "localizationController.ScheduleApply(navigationMarkerPopupSurface);",
                StringComparison.Ordinal) &&
            localizationSource.Contains(
                "comboBox.DropDownOpened += (_, _) => ScheduleComboBoxPopupApply(comboBox);",
                StringComparison.Ordinal) &&
            localizationSource.Contains(
                "popup.Child is Control child",
                StringComparison.Ordinal),
            "MainWindow is missing the language selector or a materialized-content refresh hook.");
        return "selector=16; systemRefresh=true; tabRescan=true; popupRescan=true; " +
               "comboPopupRescan=true";
    }

    private static string VerifyMobileCjkFallback()
    {
        var source = File.ReadAllText(FindProjectFile(
            "LumaChart.Avalonia",
            "Infrastructure",
            "MobileFontConfiguration.cs"));
        Require(
            source.Contains("PingFang TC", StringComparison.Ordinal) &&
            source.Contains("Hiragino Sans", StringComparison.Ordinal) &&
            source.Contains("Apple SD Gothic Neo", StringComparison.Ordinal) &&
            source.Contains("Noto Sans CJK JP", StringComparison.Ordinal) &&
            source.Contains("Noto Sans CJK KR", StringComparison.Ordinal),
            "The mobile CJK fallback chain is missing a Traditional Chinese, Japanese, or Korean family.");
        return "traditional=true; japanese=true; korean=true";
    }

    private static string FindProjectFile(params string[] relativeSegments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(
            $"Could not locate repository file: {Path.Combine(relativeSegments)}");
    }

    private static void Check(
        ICollection<WindowsLocalizationSpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsLocalizationSpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsLocalizationSpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed record WindowsLocalizationSpecResult(
    string Name,
    bool Passed,
    string Detail);
