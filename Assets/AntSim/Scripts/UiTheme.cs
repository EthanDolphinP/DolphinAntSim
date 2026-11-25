using UnityEngine;

namespace AntSim
{
    /// <summary>
    /// The look shared by every screen, matching Dolphin City Builder's interface (its "Forest" theme): a near-black
    /// green background, slightly lighter panels with thin borders, flat buttons that turn gold under the mouse,
    /// gold titles, small capitals for section headings and inset tiles for numbers.
    /// </summary>
    public static class UiTheme
    {
        static Color Hex(string hex) { ColorUtility.TryParseHtmlString(hex, out var c); return c; }

        public static readonly Color Bg = Hex("#0d1110");
        public static readonly Color Panel = Hex("#141a18");
        public static readonly Color Line = Hex("#2a3531");
        public static readonly Color Text = Hex("#cfd8d3");
        public static readonly Color Dim = Hex("#7d8a84");
        public static readonly Color Bright = Color.white;
        public static readonly Color Inset = Hex("#101614");
        public static readonly Color ButtonBg = Hex("#1d2623");
        public static readonly Color Faint = Hex("#56605b");
        public static readonly Color Highlight = Hex("#3a3520");
        public static readonly Color Accent = Hex("#e6c35c");
        public static readonly Color Good = Hex("#7fc97f");
        public static readonly Color Bad = Hex("#e07a6a");

        // For rich text: <color=...>
        public const string DimHex = "#7d8a84", AccentHex = "#e6c35c", GoodHex = "#7fc97f", BadHex = "#e07a6a",
            WarnHex = "#f0a05a", BrightHex = "#ffffff";

        public const float PanelRadius = 14f, ButtonRadius = 8f, TileRadius = 9f;

        /// <summary>Panel background, a little see-through so the colony shows behind the HUD.</summary>
        public static Color PanelAlpha(float a) => new Color(Panel.r, Panel.g, Panel.b, a);

        public static UiStyle Label(int size = 14) => new UiStyle { FontSize = size, Text = Text };
        public static UiStyle Small(int size = 12) => new UiStyle { FontSize = size, Text = Dim };

        /// <summary>A page or window title (gold).</summary>
        public static UiStyle Title(int size = 24) => new UiStyle { FontSize = size, FontStyle = FontStyle.Bold, Text = Accent };

        /// <summary>A section heading: small dim capitals.</summary>
        public static UiStyle Section(int size = 12) => new UiStyle
        {
            FontSize = size, FontStyle = FontStyle.Bold, Text = Dim, Upper = true, Height = 24f, Align = TextAnchor.LowerLeft,
        };

        public static UiStyle Button(int size = 13, float height = 30f) => new UiStyle
        {
            FontSize = size, Text = Text, Align = TextAnchor.MiddleCenter, Wrap = false, Height = height,
            Background = ButtonBg, Hover = ButtonBg, Pressed = Highlight,
            Border = Line, HoverBorder = Accent, HoverText = Accent, Radius = ButtonRadius,
            Padding = new RectOffset(10, 10, 4, 4),
        };

        /// <summary>The chosen one of a set of buttons (and the main action on a page).</summary>
        public static UiStyle On(UiStyle b)
        {
            var s = b.Copy();
            s.Background = s.Hover = Inset;
            s.Border = s.HoverBorder = Accent;
            s.Text = s.HoverText = Accent;
            s.FontStyle = FontStyle.Bold;
            return s;
        }

        /// <summary>A tab: no box until chosen or pointed at.</summary>
        public static UiStyle Tab(UiStyle b)
        {
            var s = b.Copy();
            s.Background = s.Hover = new Color(0f, 0f, 0f, 0f);
            s.Pressed = Highlight;
            s.Border = new Color(0f, 0f, 0f, 0f);
            s.HoverBorder = Line;
            return s;
        }

        /// <summary>A clickable card (a species, a list row).</summary>
        public static UiStyle Card(int size = 14) => new UiStyle
        {
            FontSize = size, Text = Text, Align = TextAnchor.UpperLeft, Wrap = true,
            Background = Inset, Hover = Inset, Pressed = Highlight,
            Border = Line, HoverBorder = Accent, Radius = TileRadius,
            Padding = new RectOffset(10, 10, 8, 8),
        };

        public static UiStyle CardOn(UiStyle card)
        {
            var s = card.Copy();
            s.Background = s.Hover = Color.Lerp(Inset, Highlight, 0.6f);
            s.Border = s.HoverBorder = Accent;
            return s;
        }

        /// <summary>A panel drawn inside a page (BeginBox).</summary>
        public static UiStyle Box() => new UiStyle
        {
            Background = Panel, Border = Line, Radius = PanelRadius, Padding = new RectOffset(16, 16, 12, 14),
        };

        /// <summary>A number tile: label in small capitals over the value.</summary>
        public static UiStyle StatBox() => new UiStyle
        {
            Background = Inset, Border = Line, Radius = TileRadius, Padding = new RectOffset(9, 9, 4, 5),
        };

        public static UiStyle StatKey() => new UiStyle { FontSize = 10, Text = Dim, Upper = true, Wrap = false };
        public static UiStyle StatValue() => new UiStyle { FontSize = 15, Text = Bright, Wrap = false };
        public static UiStyle Field(int size = 15) => new UiStyle { FontSize = size, Text = Text };
    }
}
