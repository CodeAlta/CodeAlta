using System.Collections.Immutable;
using System.Globalization;

namespace CodeAlta.Catalog;

/// <summary>
/// Lightweight string resources for i18n. English text is used as the lookup key;
/// when no translation is found, the key is returned verbatim as the English fallback.
/// </summary>
public static class SR
{
    private static string _language = "en";
    private static readonly ImmutableDictionary<string, string> s_zhCn = new Dictionary<string, string>
    {
        // === Sidebar ===
        ["Navigator"] = "导航器",
        ["Show Logs"] = "显示日志",
        ["Expand navigator"] = "展开导航器",
        ["Collapse navigator"] = "折叠导航器",
        ["Notes"] = "笔记",
        ["Clear"] = "清除",
        ["Copy notes as Markdown"] = "复制笔记为 Markdown",
        ["Clear notes"] = "清除笔记",
        ["Refresh projects and sessions"] = "刷新项目和会话",
        ["Sort projects by name"] = "按名称排序",
        ["Sort projects by last activity"] = "按最近活动排序",
        ["Workspace settings"] = "工作区设置",
        ["Show application logs"] = "显示应用日志",
        ["Expand navigator"] = "展开导航器",
        ["Collapse navigator"] = "折叠导航器",
        ["Notes"] = "笔记",
        ["Copy notes as Markdown"] = "复制笔记为 Markdown",
        ["Clear notes"] = "清除笔记",

        // === Dialog: NavigatorSettings ===
        ["Sort mode"] = "排序方式",
        ["Theme"] = "主题",
        ["Language"] = "语言",
        ["Recent sessions"] = "最近会话数",
        ["Save"] = "保存",
        ["Cancel"] = "取消",
        ["Close"] = "关闭",
        ["Esc"] = "Esc",
        ["Workspace Settings"] = "工作区设置",
        ["Configure workspace appearance and navigator behavior."] = "配置工作区外观和导航器行为。",
        ["Close the navigator settings dialog."] = "关闭导航器设置对话框。",
        ["Auto approve commands"] = "自动审批命令",
        ["Use a value from 1 to 50."] = "请输入 1 到 50 之间的值。",
        ["Enter a whole number."] = "请输入整数。",

        // === Welcome / Guidance ===
        ["Prompt ready"] = "提示就绪",
        ["Global workspace ready for a new session."] = "全局工作区已就绪，可开始新会话。",
        ["Project draft selected. Choose a project or start typing below."] = "已选择项目草稿。请在下方选择项目或开始输入。",
        ["Next session will start in {0}."] = "下一个会话将在 {0} 中开始。",
        ["Use the prompt below to start a new global session."] = "使用下方提示开始新的全局会话。",
        ["Pick a project in the sidebar before sending if you want repository context."] = "如需仓库上下文，发送前请在侧边栏中选择项目。",
        ["Reopen any session tab to continue previous work."] = "重新打开任何会话标签以继续之前的工作。",
        ["Choose a project in the sidebar or keep typing below to prepare the next session."] = "在侧边栏中选择项目，或继续输入以准备下一个会话。",
        ["Your first prompt will create the draft once a scope is selected."] = "选择作用域后，您的第一条提示将创建草稿。",
        ["Use the prompt below to start a new session for {0}."] = "使用下方提示为 {0} 开始新的会话。",
        ["Switch projects in the sidebar before sending if you want a different scope."] = "如需更改作用域，发送前请在侧边栏中切换项目。",
        ["Global draft"] = "全局草稿",
        ["Project draft"] = "项目草稿",
        ["{0} draft"] = "{0} 草稿",
        ["Draft scope selected. Send a prompt to start a global session."] = "已选择草稿作用域。发送提示以开始全局会话。",
        ["Draft scope selected. Choose a project or send a prompt to start a session."] = "已选择草稿作用域。选择项目或发送提示以开始会话。",
        ["Draft scope selected for '{0}'. Send a prompt to start a session."] = "已为 '{0}' 选择草稿作用域。发送提示以开始会话。",
        ["Send the first prompt to start a global session."] = "发送第一条提示以开始全局会话。",
        ["Send the first prompt to start a session for the selected project."] = "发送第一条提示以为所选项目开始会话。",

        // === Status ===
        ["Thinking..."] = "思考中...",
        ["Draft edited..."] = "草稿已编辑...",
        ["Thinking for {0}..."] = "已思考 {0}...",

        // === About Dialog ===
        ["About CodeAlta"] = "关于 CodeAlta",
        ["Version"] = "版本",
        ["Build"] = "构建",
        ["GitHub"] = "GitHub",
        ["Website"] = "网站",
        ["Open {0}"] = "打开 {0}",
        ["Close the about dialog."] = "关闭关于对话框。",
        ["Close shell help."] = "关闭帮助。",
        ["Shell Help"] = "帮助",
        ["Close the application logs dialog."] = "关闭应用日志对话框。",

        // === Application Logs ===
        ["Application Logs"] = "应用日志",
        ["Clear Logs"] = "清除日志",
        ["Wrap"] = "自动换行",
        ["Ctrl+F Search"] = "Ctrl+F 搜索",

        // === Global Commands ===
        ["Exit"] = "退出",
        ["Quit CodeAlta."] = "退出 CodeAlta。",

        // === Agent / Model Selectors ===
        ["Open the agent prompts dialog."] = "打开代理提示对话框。",
        ["Open the models dialog."] = "打开模型对话框。",
        ["Compact the selected session when it is idle (Ctrl+F11)."] = "在选定会话空闲时压缩（Ctrl+F11）。",

        // === Image Attachment ===
        ["Add To Prompt"] = "添加到提示",
        ["Rename"] = "重命名",
        ["Delete"] = "删除",
        ["Title"] = "标题",
    }.ToImmutableDictionary();

    /// <summary>
    /// Gets or sets the active language. Use "en" or "zh-CN".
    /// </summary>
    public static string Language
    {
        get => _language;
        set => _language = string.IsNullOrWhiteSpace(value) ? "en" : value;
    }

    /// <summary>
    /// Looks up a translated string. Falls back to the key itself when no translation exists.
    /// </summary>
    public static string T(string key)
    {
        if (_language.StartsWith("zh") && s_zhCn.TryGetValue(key, out var t))
            return t;
        return key;
    }

    /// <summary>
    /// Looks up a format string and applies arguments.
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        return args is { Length: > 0 } ? string.Format(template, args) : template;
    }

    /// <summary>
    /// Auto-detects the language from the current UI culture.
    /// </summary>
    public static void AutoDetect()
    {
        try
        {
            var name = CultureInfo.CurrentUICulture.Name;
            Language = name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        }
        catch
        {
            Language = "en";
        }
    }
}
