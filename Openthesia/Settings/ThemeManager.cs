using System.Numerics;
using Openthesia.Core;
using Openthesia.Enums;

namespace Openthesia.Settings;

public static class ThemeManager
{
    public static Themes Theme { get; private set; } = Themes.Sky;
    public static Vector4 MainBgCol = ImGuiTheme.HtmlToVec4("#1F2937");
    public static Vector4 RightHandCol = ImGuiTheme.HtmlToVec4("#15CB44");
    public static Vector4 LeftHandCol = ImGuiTheme.HtmlToVec4("#15CB44");

    public static Vector4 NoteFadeCol => RightHandCol;

    public static void SetNoteFadeColor(Vector4 color)
    {
        RightHandCol = color;
        LeftHandCol = color;
    }

    public static void SetTheme(Themes theme)
    {
        switch (theme)
        {
            case Themes.Sky:
                MainBgCol = ImGuiTheme.HtmlToVec4("#1F2937");
                SetNoteFadeColor(ImGuiTheme.HtmlToVec4("#15CB44"));
                break;

            case Themes.Volcano:
                MainBgCol = ImGuiTheme.HtmlToVec4("#151617");
                SetNoteFadeColor(ImGuiTheme.HtmlToVec4("#E51C1C"));
                break;
            case Themes.Synthesia:
                MainBgCol = ImGuiTheme.HtmlToVec4("#313131");
                SetNoteFadeColor(ImGuiTheme.HtmlToVec4("#87C853"));
                break;
        }
        Theme = theme;
        ImGuiTheme.PushTheme();
    }
}
