using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Starshot.Frameworks;
using Starshot.Language;

namespace Starshot.Features.Setting;

/// <summary>
/// 标注编辑器设置页：截图后是否打开编辑器 + 各工具的启用开关与工具栏显示顺序
///（顺序与编辑器内拖拽换序共用 ScreenshotEditToolbarOrder 配置，双向同步）。
/// </summary>
public sealed partial class EditorSetting : PageBase
{
    /// <summary>工具默认顺序（与 ScreenshotEditWindow.ToolButtonByName 一致）</summary>
    private static readonly string[] DefaultToolOrder =
    [
        "Blur",
        "Highlight",
        "Brush",
        "Shapes",
        "Erase",
        "Select",
    ];

    /// <summary>截图后打开标注编辑器（默认开，保持历史行为）</summary>
    public bool OpenEditorAfterCapture
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.ScreenshotEditorOpenAfterCapture = value;
            }
        }
    } = AppConfig.ScreenshotEditorOpenAfterCapture;

    /// <summary>工具行（按编辑器当前顺序；新增工具不在旧顺序中时按默认顺序追加）</summary>
    public ObservableCollection<EditorToolItem> Tools { get; } = new();

    public EditorSetting()
    {
        InitializeComponent();
        BuildToolList();
    }

    private void BuildToolList()
    {
        var disabled = AppConfig.ScreenshotEditDisabledTools;
        var order = new List<string>();
        // 保存顺序过滤掉未知条目，缺失的按默认顺序补齐
        foreach (string name in AppConfig.ScreenshotEditToolbarOrder)
        {
            if (Array.IndexOf(DefaultToolOrder, name) >= 0 && !order.Contains(name))
            {
                order.Add(name);
            }
        }
        foreach (string name in DefaultToolOrder)
        {
            if (!order.Contains(name))
            {
                order.Add(name);
            }
        }
        foreach (string name in order)
        {
            Tools.Add(
                new EditorToolItem(
                    name,
                    ToolDisplayName(name),
                    !disabled.Contains(name),
                    v => SetToolEnabled(name, v),
                    up => MoveTool(name, up)
                )
            );
        }
    }

    private static string ToolDisplayName(string name)
    {
        return name switch
        {
            "Blur" => Lang.ScreenshotEditWindow_Blur,
            "Highlight" => Lang.ScreenshotEditWindow_Highlight,
            "Brush" => Lang.ScreenshotEditWindow_Brush,
            "Shapes" => Lang.ScreenshotEditWindow_Shapes,
            "Erase" => Lang.ScreenshotEditWindow_Erase,
            "Select" => Lang.ScreenshotEditWindow_Select,
            _ => name,
        };
    }

    private void SetToolEnabled(string name, bool enabled)
    {
        var disabled = AppConfig.ScreenshotEditDisabledTools.Where(n => n != name).ToList();
        if (!enabled)
        {
            disabled.Add(name);
        }
        AppConfig.ScreenshotEditDisabledTools = disabled;
    }

    /// <summary>相邻换位并写回共享顺序配置（编辑器下次打开按此排布）。</summary>
    private void MoveTool(string name, bool up)
    {
        var names = Tools.Select(t => t.Name).ToList();
        int i = names.IndexOf(name);
        int j = up ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= names.Count)
        {
            return;
        }
        (names[i], names[j]) = (names[j], names[i]);
        Tools.Move(i, j);
        AppConfig.ScreenshotEditToolbarOrder = names;
    }
}

/// <summary>编辑器工具行（名称 + 启用开关 + 上移/下移）。</summary>
public sealed class EditorToolItem : ObservableObject
{
    private readonly Action<bool> _onEnabled;

    private readonly Action<bool> _onMove;

    public string Name { get; }

    public string DisplayText { get; }

    public bool IsEnabled
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                _onEnabled(value);
            }
        }
    }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public EditorToolItem(
        string name,
        string displayText,
        bool isEnabled,
        Action<bool> onEnabled,
        Action<bool> onMove
    )
    {
        Name = name;
        DisplayText = displayText;
        _onEnabled = onEnabled;
        _onMove = onMove;
        MoveUpCommand = new RelayCommand(() => _onMove(true));
        MoveDownCommand = new RelayCommand(() => _onMove(false));
        IsEnabled = isEnabled;
    }
}
