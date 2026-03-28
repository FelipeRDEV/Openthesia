using ImGuiNET;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Openthesia.Core;
using Openthesia.Settings;
using Openthesia.Ui.Helpers;
using System.Numerics;

namespace Openthesia.Ui;

public class PianoRenderer
{
    static uint _black = ImGui.GetColorU32(ImGuiTheme.HtmlToVec4("#141414"));
    static uint _white = ImGui.GetColorU32(ImGuiTheme.HtmlToVec4("#FFFFFF"));
    static uint _whitePressed = ImGui.GetColorU32(ImGuiTheme.HtmlToVec4("#888888"));
    static uint _blackPressed = ImGui.GetColorU32(ImGuiTheme.HtmlToVec4("#555555"));
    static uint _whiteKeyLabel = ImGui.GetColorU32(ImGuiTheme.HtmlToVec4("#4C4C4C"));
    static uint _blackKeyLabel = ImGui.GetColorU32(ImGuiTheme.HtmlToVec4("#F2F2F2"));
    static uint _labelLightShadow = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.65f));
    static uint _labelDarkShadow = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));

    public static float Width;
    public static float Height;
    public static Vector2 P;

    public static Dictionary<SevenBitNumber, int> WhiteNoteToKey = new();
    public static Dictionary<SevenBitNumber, int> BlackNoteToKey = new();

    private static void DrawKeyLabel(ImDrawListPtr drawList, Vector2 pos, string text, uint color, uint shadowColor, float fontSize)
    {
        drawList.AddText(ImGui.GetFont(), fontSize, pos + new Vector2(1, 1), shadowColor, text);
        drawList.AddText(ImGui.GetFont(), fontSize, pos, color, text);
    }

    private static bool TryGetFittedTextSize(string text, float maxWidth, float maxHeight, out float fontSize, out Vector2 textSize)
    {
        const float minFontSize = 6f;
        float baseFontSize = ImGui.GetFontSize();
        Vector2 baseTextSize = ImGui.CalcTextSize(text);

        if (baseTextSize.X <= 0f || baseTextSize.Y <= 0f || baseFontSize <= 0f)
        {
            fontSize = 0f;
            textSize = Vector2.Zero;
            return false;
        }

        float widthScale = maxWidth / baseTextSize.X;
        float heightScale = maxHeight / baseTextSize.Y;
        float scale = MathF.Min(1f, MathF.Min(widthScale, heightScale));

        if (scale <= 0f)
        {
            fontSize = 0f;
            textSize = Vector2.Zero;
            return false;
        }

        fontSize = MathF.Max(minFontSize, baseFontSize * scale);
        float realScale = fontSize / baseFontSize;
        textSize = baseTextSize * realScale;
        return true;
    }

    public static void RenderKeyboard()
    {
        ImGui.PushFont(FontController.Font16_Icon12);
        ImDrawListPtr draw_list = ImGui.GetWindowDrawList();
        P = ImGui.GetCursorScreenPos();

        Width = ImGui.GetIO().DisplaySize.X * 1.9f / 100;
        Height = ImGui.GetIO().DisplaySize.Y - ImGui.GetIO().DisplaySize.Y * 76f / 100;

        int cur_key = 22; // Start from first black key since we need to handle black keys mouse input before white ones

        /* Check if a black key is pressed */
        bool blackKeyClicked = false;
        for (int key = 0; key < 52; key++)
        {
            if (KeysUtils.HasBlack(key))
            {
                Vector2 min = new(P.X + key * Width + Width * 3 / 4, P.Y);
                Vector2 max = new(P.X + key * Width + Width * 5 / 4 + 1, P.Y + Height / 1.5f);

                if (ImGui.IsMouseHoveringRect(min, max) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    blackKeyClicked = true;
                }

                cur_key += 2;
            }
            else
            {
                cur_key++;
            }
        }

        cur_key = 21;
        for (int key = 0; key < 52; key++)
        {
            uint col = _white;

            if (ImGui.IsMouseHoveringRect(new(P.X + key * Width, P.Y), new(P.X + key * Width + Width, P.Y + Height)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && !CoreSettings.KeyboardInput && !blackKeyClicked)
            {
                // on key mouse press
                IOHandle.OnEventReceived(null,
                    new Melanchall.DryWetMidi.Multimedia.MidiEventReceivedEventArgs(new NoteOnEvent((SevenBitNumber)cur_key, new SevenBitNumber(127))));
                DevicesManager.ODevice?.SendEvent(new NoteOnEvent((SevenBitNumber)cur_key, new SevenBitNumber(127)));
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !CoreSettings.KeyboardInput)
            {
                if (IOHandle.PressedKeys.Contains(cur_key))
                {
                    // on key mouse release
                    IOHandle.OnEventReceived(null,
                        new Melanchall.DryWetMidi.Multimedia.MidiEventReceivedEventArgs(new NoteOffEvent((SevenBitNumber)cur_key, new SevenBitNumber(0))));
                    DevicesManager.ODevice?.SendEvent(new NoteOffEvent((SevenBitNumber)cur_key, new SevenBitNumber(0)));
                }
            }

            if (IOHandle.PressedKeys.Contains(cur_key))
            {
                var color = CoreSettings.KeyPressColorMatch ? ImGui.GetColorU32(ThemeManager.RightHandCol) : _whitePressed;
                col = color;
            }

            var offset = IOHandle.PressedKeys.Contains(cur_key) ? 2 : 0;

            draw_list.AddImageRounded(Drawings.C,
                new Vector2(P.X + key * Width, P.Y) + new Vector2(offset, 0),
                new Vector2(P.X + key * Width + Width, P.Y + Height) + new Vector2(offset, 0), Vector2.Zero, Vector2.One, col, 5, ImDrawFlags.RoundCornersBottom);

            if (WhiteNoteToKey.Count < 52)
                WhiteNoteToKey.Add((SevenBitNumber)cur_key, key);

            string whiteNoteLabel = KeysUtils.GetMidiNoteLabel(cur_key);
            if (!TryGetFittedTextSize(whiteNoteLabel, Width - 4f, Height - 16f, out float whiteFontSize, out Vector2 whiteTextSize))
            {
                whiteFontSize = ImGui.GetFontSize();
                whiteTextSize = ImGui.CalcTextSize(whiteNoteLabel);
            }
            Vector2 whiteTextPos = new(
                P.X + key * Width + Width / 2 - whiteTextSize.X / 2 + offset,
                P.Y + Height - whiteTextSize.Y - 14 * FontController.DSF);
            DrawKeyLabel(draw_list, whiteTextPos, whiteNoteLabel, _whiteKeyLabel, _labelLightShadow, whiteFontSize);

            cur_key++;
            if (KeysUtils.HasBlack(key))
            {
                cur_key++;
            }
        }

        cur_key = 22;
        for (int key = 0; key < 52; key++)
        {
            if (BlackNoteToKey.Count < 52)
                BlackNoteToKey.Add((SevenBitNumber)cur_key, key);

            if (KeysUtils.HasBlack(key))
            {
                uint col = ImGui.GetColorU32(Vector4.One);

                if (ImGui.IsMouseHoveringRect(new(P.X + key * Width + Width * 3 / 4, P.Y),
                    new(P.X + key * Width + Width * 5 / 4 + 1, P.Y + Height / 1.5f)) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                    && !CoreSettings.KeyboardInput)
                {
                    IOHandle.OnEventReceived(null,
                        new Melanchall.DryWetMidi.Multimedia.MidiEventReceivedEventArgs(new NoteOnEvent((SevenBitNumber)cur_key, new SevenBitNumber(127))));
                    DevicesManager.ODevice?.SendEvent(new NoteOnEvent((SevenBitNumber)cur_key, new SevenBitNumber(127)));
                }

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !CoreSettings.KeyboardInput)
                {
                    if (IOHandle.PressedKeys.Contains(cur_key))
                    {
                        IOHandle.OnEventReceived(null,
                            new Melanchall.DryWetMidi.Multimedia.MidiEventReceivedEventArgs(new NoteOffEvent((SevenBitNumber)cur_key, new SevenBitNumber(0))));
                        DevicesManager.ODevice?.SendEvent(new NoteOffEvent((SevenBitNumber)cur_key, new SevenBitNumber(0)));
                    }
                }

                if (IOHandle.PressedKeys.Contains(cur_key))
                {
                    var v3 = new Vector3(ThemeManager.RightHandCol.X, ThemeManager.RightHandCol.Y, ThemeManager.RightHandCol.Z);
                    var color = CoreSettings.KeyPressColorMatch ? ImGui.GetColorU32(new Vector4(v3, 1)) : _blackPressed;
                    col = color;
                }

                var offset = IOHandle.PressedKeys.Contains(cur_key) ? 1 : 0;
                var blackImage = IOHandle.PressedKeys.Contains(cur_key) ? Drawings.CSharpWhite : Drawings.CSharp;

                draw_list.AddImage(blackImage,
                    new Vector2(P.X + key * Width + Width * 3 / 4, P.Y),
                    new Vector2(P.X + key * Width + Width * 5 / 4 + 1, P.Y + Height / 1.5f) + new Vector2(offset), Vector2.Zero, Vector2.One, col);

                string blackNoteLabel = KeysUtils.GetMidiNoteLabel(cur_key);
                float blackMinX = P.X + key * Width + Width * 3 / 4;
                float blackMaxX = P.X + key * Width + Width * 5 / 4 + 1;
                float blackBoxWidth = blackMaxX - blackMinX - 2f;
                float blackBoxHeight = Height / 1.5f - 8f;
                if (!TryGetFittedTextSize(blackNoteLabel, blackBoxWidth, blackBoxHeight, out float blackFontSize, out Vector2 blackTextSize))
                {
                    blackFontSize = ImGui.GetFontSize();
                    blackTextSize = ImGui.CalcTextSize(blackNoteLabel);
                }

                Vector2 blackTextPos = new(
                    blackMinX + (blackMaxX - blackMinX - blackTextSize.X) / 2 + offset,
                    P.Y + Height / 1.5f - blackTextSize.Y - 6 * FontController.DSF);
                DrawKeyLabel(draw_list, blackTextPos, blackNoteLabel, _blackKeyLabel, _labelDarkShadow, blackFontSize);

                cur_key += 2;
            }
            else
            {
                cur_key++;
            }
        }

        ImGui.PopFont();
    }
}
