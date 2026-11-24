using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AntSim
{
    /// <summary>Look of one kind of UI element.</summary>
    public class UiStyle
    {
        public int FontSize = 14;
        public FontStyle FontStyle = FontStyle.Normal;
        public Color Text = new Color(0.92f, 0.92f, 0.94f);
        public TextAnchor Align = TextAnchor.MiddleLeft;
        public bool Wrap = true;          // false: one line, as wide as its text
        public Color Background = Color.clear;
        public Color Hover = Color.clear;
        public Color Pressed = Color.clear;
        public float Height;              // minimum height (0 = from the text)
        public RectOffset Padding = new RectOffset(4, 4, 2, 2);

        public UiStyle Copy() => (UiStyle)MemberwiseClone();
    }

    /// <summary>
    /// A tiny immediate-mode layer over Unity UI (uGUI). Screens are written like IMGUI —
    /// <c>if (ui.Button("Go")) ...</c> every frame — but are drawn by real Canvas elements, which are
    /// created on first use and then reused, so nothing is rebuilt unless its text or style changes.
    /// Clicks and slider drags are reported on the next frame's call.
    /// </summary>
    public class UiKit : MonoBehaviour
    {
        // ------------------------------------------------------------------ setup

        public Canvas Canvas { get; private set; }
        public RectTransform Root { get; private set; }
        public Font Font { get; private set; }
        /// <summary>UI scale factor (set every frame by the owner).</summary>
        public float Scale { get => Canvas.scaleFactor; set => Canvas.scaleFactor = Mathf.Max(0.3f, value); }
        /// <summary>Screen size in UI units.</summary>
        public Vector2 VirtualSize => new Vector2(Screen.width, Screen.height) / Canvas.scaleFactor;

        static Sprite whiteSprite;

        /// <summary>Is the mouse over any UI element that catches clicks?</summary>
        public static bool PointerOverUi =>
            EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        /// <summary>Does any text box have the keyboard?</summary>
        public static bool AnyInputFocused
        {
            get
            {
                var es = EventSystem.current;
                if (es == null || es.currentSelectedGameObject == null) return false;
                var f = es.currentSelectedGameObject.GetComponent<InputField>();
                return f != null && f.isFocused;
            }
        }

        public static UiKit Create(Transform owner, string name, int sortOrder)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(owner, false);
            var kit = go.AddComponent<UiKit>();
            kit.Init(sortOrder);
            return kit;
        }

        void Init(int sortOrder)
        {
            Canvas = gameObject.AddComponent<Canvas>();
            Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Canvas.sortingOrder = sortOrder;
            Canvas.pixelPerfect = false;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            gameObject.AddComponent<GraphicRaycaster>();
            Root = (RectTransform)transform;
            Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            if (EventSystem.current == null && FindObjectOfType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                DontDestroyOnLoad(es);
            }
            if (whiteSprite == null)
            {
                var tex = Texture2D.whiteTexture;
                whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f,
                    0, SpriteMeshType.FullRect, new Vector4(1, 1, 1, 1));
            }
        }

        void LateUpdate()
        {
            // Buttons and sliders keep no keyboard focus after a click: in Unity's default input settings Space is also
            // "Submit", so pausing with Space would press the last button clicked again. Text boxes keep their focus.
            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            if (sel != null && sel.GetComponent<InputField>() == null && !es.alreadySelecting)
                es.SetSelectedGameObject(null);
        }

        // ------------------------------------------------------------------ panels

        /// <summary>A free-floating box of elements (a window, a bar, a dialog).</summary>
        public class Panel
        {
            public RectTransform Rect;
            public Image Background;
            internal Container Body;
            public bool Visible { get => Rect.gameObject.activeSelf; set { if (Rect.gameObject.activeSelf != value) Rect.gameObject.SetActive(value); } }
        }

        /// <summary>
        /// Makes a panel. Anchor/pivot place it on screen; with fitHeight it grows to its content.
        /// With vertical = false its elements go left to right (a toolbar).
        /// </summary>
        public Panel CreatePanel(string name, Vector2 anchor, Vector2 pivot, Color background, RectOffset padding,
            bool vertical = true, bool fitHeight = true, bool fitWidth = false, bool draggable = false)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            var img = go.AddComponent<Image>();
            img.sprite = whiteSprite;
            img.color = background;
            img.raycastTarget = background.a > 0f;
            HorizontalOrVerticalLayoutGroup g = vertical ? go.AddComponent<VerticalLayoutGroup>() : go.AddComponent<HorizontalLayoutGroup>();
            Configure(g, padding, 4f);
            if (!vertical) g.childAlignment = TextAnchor.MiddleLeft;
            if (fitHeight || fitWidth)
            {
                var fit = go.AddComponent<ContentSizeFitter>();
                if (fitHeight) fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                if (fitWidth) fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            }
            if (draggable) go.AddComponent<UiDrag>().Target = rt;
            return new Panel { Rect = rt, Background = img, Body = new Container { Rect = rt, Group = g, Vertical = vertical } };
        }

        static void Configure(HorizontalOrVerticalLayoutGroup g, RectOffset padding, float spacing)
        {
            g.padding = padding;
            g.spacing = spacing;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = g is VerticalLayoutGroup;
            g.childForceExpandHeight = false;
            g.childAlignment = TextAnchor.UpperLeft;
        }

        // ------------------------------------------------------------------ immediate API

        internal enum Kind { Label, Button, Slider, Toggle, Input, Space, Flex, Row, Scroll, Swatch, Column }

        internal class Container
        {
            public RectTransform Rect;
            public HorizontalOrVerticalLayoutGroup Group;
            public bool Vertical;
            public readonly List<Node> Nodes = new List<Node>();
            public int Cursor;
        }

        internal class Node
        {
            public Kind Kind;
            public RectTransform Rect;
            public LayoutElement Layout;
            public Text Text;
            public Image Image;
            public Button Button;
            public Slider Slider;
            public Toggle Toggle;
            public InputField Input;
            public ScrollRect Scroll;
            public Container Child;
            public UiStyle Style;
            public bool Clicked, Changed;
            public bool Syncing; // the code (not the user) is changing the widget: ignore its change events
            public float Value;
            public bool On;
            public string Str;
            public float Width = -1f;
        }

        readonly Stack<Container> stack = new Stack<Container>();
        Container Current => stack.Peek();

        /// <summary>Start filling a panel for this frame.</summary>
        public void Begin(Panel p)
        {
            stack.Clear();
            p.Body.Cursor = 0;
            stack.Push(p.Body);
        }

        /// <summary>Finish the panel: elements not used this frame are hidden.</summary>
        public void End()
        {
            while (stack.Count > 0) Close(stack.Pop());
        }

        static void Close(Container c)
        {
            for (int i = c.Cursor; i < c.Nodes.Count; i++)
                if (c.Nodes[i].Rect.gameObject.activeSelf) c.Nodes[i].Rect.gameObject.SetActive(false);
        }

        Node Next(Kind kind)
        {
            var c = Current;
            if (c.Cursor < c.Nodes.Count && c.Nodes[c.Cursor].Kind != kind)
            {
                // The layout changed shape here: drop this element and everything after it.
                for (int i = c.Cursor; i < c.Nodes.Count; i++) Destroy(c.Nodes[i].Rect.gameObject);
                c.Nodes.RemoveRange(c.Cursor, c.Nodes.Count - c.Cursor);
            }
            Node n;
            if (c.Cursor < c.Nodes.Count) n = c.Nodes[c.Cursor];
            else
            {
                n = Build(kind, c);
                c.Nodes.Add(n);
            }
            c.Cursor++;
            if (!n.Rect.gameObject.activeSelf) n.Rect.gameObject.SetActive(true);
            return n;
        }

        void SetWidth(Node n, float width, float flexible)
        {
            if (n.Width == width && n.Layout.flexibleWidth == flexible) return;
            n.Width = width;
            n.Layout.preferredWidth = width > 0f ? width : -1f;
            n.Layout.minWidth = width > 0f ? width : -1f;
            n.Layout.flexibleWidth = width > 0f ? 0f : flexible;
        }

        public void Label(string text, UiStyle style, float width = -1f)
        {
            var n = Next(Kind.Label);
            ApplyText(n, text, style);
            SetWidth(n, width, Current.Vertical ? 1f : style.Wrap ? 100f : 0f);
        }

        public bool Button(string text, UiStyle style, float width = -1f)
        {
            var n = Next(Kind.Button);
            ApplyText(n, text, style);
            ApplyBackground(n, style);
            SetWidth(n, width, 1f);
            bool clicked = n.Clicked;
            n.Clicked = false;
            return clicked;
        }

        /// <summary>A coloured square; outlined when selected.</summary>
        public bool Swatch(Color color, bool selected, float size)
        {
            var n = Next(Kind.Swatch);
            var inner = (Image)n.Rect.GetChild(0).GetComponent<Image>();
            inner.color = color;
            n.Image.color = selected ? Color.white : new Color(1f, 1f, 1f, 0.08f);
            n.Layout.preferredWidth = n.Layout.minWidth = size;
            n.Layout.preferredHeight = n.Layout.minHeight = size;
            n.Layout.flexibleWidth = 0f;
            bool clicked = n.Clicked;
            n.Clicked = false;
            return clicked;
        }

        public float Slider(float value, float min, float max)
        {
            var n = Next(Kind.Slider);
            if (n.Slider.minValue != min || n.Slider.maxValue != max)
            {
                // A new range means this widget was just built or is being reused for another setting. Unity clamps
                // the value (and fires onValueChanged) while the range changes; that is not the user moving it.
                n.Syncing = true;
                n.Slider.minValue = Mathf.Min(min, max);
                n.Slider.maxValue = Mathf.Max(min, max);
                n.Slider.minValue = min;
                n.Syncing = false;
                n.Changed = false;
                n.Slider.SetValueWithoutNotify(value);
                return value;
            }
            if (n.Changed)
            {
                n.Changed = false;
                return n.Slider.value;
            }
            if (!Mathf.Approximately(n.Slider.value, value)) n.Slider.SetValueWithoutNotify(value);
            return value;
        }

        public bool Toggle(bool value, string text, UiStyle style)
        {
            var n = Next(Kind.Toggle);
            if (n.Str != text) { n.Str = text; n.Changed = false; } // reused for a different toggle
            ApplyText(n, text, style);
            if (n.Changed)
            {
                n.Changed = false;
                return n.Toggle.isOn;
            }
            if (n.Toggle.isOn != value) n.Toggle.SetIsOnWithoutNotify(value);
            return value;
        }

        public string TextField(string value, int maxLength, UiStyle style)
        {
            var n = Next(Kind.Input);
            n.Input.characterLimit = maxLength;
            if (n.Text.fontSize != style.FontSize) n.Text.fontSize = style.FontSize;
            if (n.Changed)
            {
                n.Changed = false;
                return n.Input.text;
            }
            if (!n.Input.isFocused && n.Input.text != (value ?? "")) n.Input.SetTextWithoutNotify(value ?? "");
            return value;
        }

        public void Space(float pixels)
        {
            var n = Next(Kind.Space);
            if (Current.Vertical) { n.Layout.minHeight = n.Layout.preferredHeight = pixels; n.Layout.minWidth = 0; }
            else { n.Layout.minWidth = n.Layout.preferredWidth = pixels; n.Layout.minHeight = 0; }
        }

        public void FlexibleSpace()
        {
            var n = Next(Kind.Flex);
            n.Layout.flexibleWidth = Current.Vertical ? 0f : 1f;
            n.Layout.flexibleHeight = Current.Vertical ? 1f : 0f;
        }

        /// <summary>Elements side by side until EndRow.</summary>
        public void BeginRow(float height = -1f)
        {
            var n = Next(Kind.Row);
            n.Layout.minHeight = height;
            n.Child.Cursor = 0;
            stack.Push(n.Child);
        }

        public void EndRow() => Close(stack.Pop());

        /// <summary>A scrolling area at most maxHeight tall (shrinks to its content).</summary>
        public void BeginScroll(float maxHeight, bool resetToTop = false)
        {
            var n = Next(Kind.Scroll);
            float content = LayoutUtility.GetPreferredHeight(n.Child.Rect);
            float h = Mathf.Min(maxHeight, Mathf.Max(20f, content));
            n.Layout.preferredHeight = n.Layout.minHeight = h;
            n.Layout.flexibleHeight = 0f;
            if (resetToTop) n.Scroll.verticalNormalizedPosition = 1f;
            n.Child.Cursor = 0;
            stack.Push(n.Child);
        }

        public void EndScroll() => Close(stack.Pop());

        // ------------------------------------------------------------------ building

        Node Build(Kind kind, Container parent)
        {
            var go = new GameObject(kind.ToString(), typeof(RectTransform));
            go.transform.SetParent(parent.Rect, false);
            var n = new Node { Kind = kind, Rect = (RectTransform)go.transform };
            n.Layout = go.AddComponent<LayoutElement>();
            switch (kind)
            {
                case Kind.Label:
                    n.Text = AddText(go);
                    break;
                case Kind.Button:
                {
                    n.Image = go.AddComponent<Image>();
                    n.Image.sprite = whiteSprite;
                    n.Button = go.AddComponent<Button>();
                    n.Button.targetGraphic = n.Image;
                    n.Button.onClick.AddListener(() => n.Clicked = true);
                    var g = go.AddComponent<VerticalLayoutGroup>();
                    Configure(g, new RectOffset(8, 8, 4, 4), 0f);
                    var t = new GameObject("Text", typeof(RectTransform));
                    t.transform.SetParent(go.transform, false);
                    n.Text = AddText(t);
                    break;
                }
                case Kind.Swatch:
                {
                    n.Image = go.AddComponent<Image>();
                    n.Image.sprite = whiteSprite;
                    var b = go.AddComponent<Button>();
                    b.targetGraphic = n.Image;
                    b.transition = Selectable.Transition.None;
                    b.onClick.AddListener(() => n.Clicked = true);
                    var inner = new GameObject("Colour", typeof(RectTransform));
                    inner.transform.SetParent(go.transform, false);
                    var irt = (RectTransform)inner.transform;
                    irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one;
                    irt.offsetMin = new Vector2(5, 5); irt.offsetMax = new Vector2(-5, -5);
                    var ii = inner.AddComponent<Image>();
                    ii.sprite = whiteSprite;
                    ii.raycastTarget = false;
                    break;
                }
                case Kind.Slider: BuildSlider(n, go); break;
                case Kind.Toggle: BuildToggle(n, go); break;
                case Kind.Input: BuildInput(n, go); break;
                case Kind.Space:
                case Kind.Flex:
                    break;
                case Kind.Row:
                case Kind.Column:
                {
                    var g = kind == Kind.Row ? (HorizontalOrVerticalLayoutGroup)go.AddComponent<HorizontalLayoutGroup>()
                        : go.AddComponent<VerticalLayoutGroup>();
                    Configure(g, new RectOffset(0, 0, 0, 0), 4f);
                    g.childAlignment = TextAnchor.MiddleLeft;
                    n.Child = new Container { Rect = n.Rect, Group = g, Vertical = kind == Kind.Column };
                    n.Layout.flexibleWidth = 1f;
                    break;
                }
                case Kind.Scroll: BuildScroll(n, go); break;
            }
            // No keyboard navigation: otherwise a slider or button clicked earlier stays selected and the arrow keys /
            // WASD (which pan the camera) move it too - e.g. panning left dragged the last-touched slider to 0.
            foreach (var sel in go.GetComponentsInChildren<Selectable>(true))
                sel.navigation = new Navigation { mode = Navigation.Mode.None };
            return n;
        }

        Text AddText(GameObject go)
        {
            var t = go.AddComponent<Text>();
            t.font = Font;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        void ApplyText(Node n, string text, UiStyle s)
        {
            if (n.Text.text != text) n.Text.text = text;
            if (n.Style == s) return;
            n.Style = s;
            n.Text.fontSize = s.FontSize;
            n.Text.fontStyle = s.FontStyle;
            n.Text.color = s.Text;
            n.Text.alignment = s.Align;
            n.Text.horizontalOverflow = s.Wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            n.Layout.minHeight = s.Height > 0f ? s.Height : -1f;
            if (n.Kind == Kind.Label && n.Text.gameObject == n.Rect.gameObject)
            {
                // Labels get their padding from a layout group on demand: keep it simple with a min height only.
            }
        }

        void ApplyBackground(Node n, UiStyle s)
        {
            if (n.Image == null || n.Button == null) return;
            var cb = n.Button.colors;
            if (cb.normalColor == s.Background && cb.highlightedColor == s.Hover) return;
            n.Image.color = Color.white;
            cb.normalColor = s.Background;
            cb.highlightedColor = s.Hover.a > 0f ? s.Hover : s.Background;
            cb.selectedColor = s.Background;
            cb.pressedColor = s.Pressed.a > 0f ? s.Pressed : cb.highlightedColor;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.06f;
            n.Button.colors = cb;
            var g = n.Rect.GetComponent<VerticalLayoutGroup>();
            if (g != null) g.padding = s.Padding;
        }

        void BuildSlider(Node n, GameObject go)
        {
            n.Layout.minHeight = n.Layout.preferredHeight = 20f;
            n.Layout.flexibleWidth = 1f;
            var bg = Child(go, "Background", new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, -3), new Vector2(0, 3));
            var bgImg = bg.AddComponent<Image>(); bgImg.sprite = whiteSprite; bgImg.color = new Color(0.22f, 0.22f, 0.27f);
            var fillArea = Child(go, "Fill Area", new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, -3), new Vector2(-8, 3));
            var fill = Child(fillArea, "Fill", Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(8, 0));
            var fillImg = fill.AddComponent<Image>(); fillImg.sprite = whiteSprite; fillImg.color = new Color(0.36f, 0.52f, 0.78f);
            var handleArea = Child(go, "Handle Area", Vector2.zero, Vector2.one, new Vector2(7, 0), new Vector2(-7, 0));
            var handle = Child(handleArea, "Handle", Vector2.zero, new Vector2(0, 1), new Vector2(-7, 2), new Vector2(7, -2));
            var hImg = handle.AddComponent<Image>(); hImg.sprite = whiteSprite; hImg.color = new Color(0.9f, 0.92f, 0.96f);
            n.Slider = go.AddComponent<Slider>();
            n.Slider.fillRect = (RectTransform)fill.transform;
            n.Slider.handleRect = (RectTransform)handle.transform;
            n.Slider.targetGraphic = hImg;
            n.Slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            n.Slider.onValueChanged.AddListener(_ => { if (!n.Syncing) n.Changed = true; });
            // Clicks on the track should land on the slider, not the map.
            bgImg.raycastTarget = true;
        }

        void BuildToggle(Node n, GameObject go)
        {
            var h = go.AddComponent<HorizontalLayoutGroup>();
            Configure(h, new RectOffset(2, 2, 2, 2), 8f);
            h.childAlignment = TextAnchor.MiddleLeft;
            var box = new GameObject("Box", typeof(RectTransform));
            box.transform.SetParent(go.transform, false);
            var boxLe = box.AddComponent<LayoutElement>();
            boxLe.minWidth = boxLe.preferredWidth = 18f; boxLe.minHeight = boxLe.preferredHeight = 18f;
            var boxImg = box.AddComponent<Image>(); boxImg.sprite = whiteSprite; boxImg.color = new Color(0.22f, 0.22f, 0.27f);
            var check = Child(box, "Check", Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-4, -4));
            var checkImg = check.AddComponent<Image>(); checkImg.sprite = whiteSprite; checkImg.color = new Color(0.5f, 0.72f, 1f);
            var label = new GameObject("Label", typeof(RectTransform));
            label.transform.SetParent(go.transform, false);
            n.Text = AddText(label);
            label.AddComponent<LayoutElement>().flexibleWidth = 1f;
            n.Toggle = go.AddComponent<Toggle>();
            n.Toggle.targetGraphic = boxImg;
            n.Toggle.graphic = checkImg;
            n.Toggle.onValueChanged.AddListener(_ => n.Changed = true);
            // The whole row is clickable.
            var hit = go.AddComponent<Image>(); hit.sprite = whiteSprite; hit.color = Color.clear;
        }

        void BuildInput(Node n, GameObject go)
        {
            n.Layout.minHeight = n.Layout.preferredHeight = 32f;
            var img = go.AddComponent<Image>(); img.sprite = whiteSprite; img.color = new Color(0.16f, 0.16f, 0.2f);
            var textGo = Child(go, "Text", Vector2.zero, Vector2.one, new Vector2(8, 2), new Vector2(-8, -2));
            n.Text = AddText(textGo);
            n.Text.supportRichText = false;
            n.Text.alignment = TextAnchor.MiddleLeft;
            n.Text.color = new Color(0.95f, 0.95f, 0.97f);
            n.Input = go.AddComponent<InputField>();
            n.Input.textComponent = n.Text;
            n.Input.targetGraphic = img;
            n.Input.lineType = InputField.LineType.SingleLine;
            n.Input.onValueChanged.AddListener(_ => n.Changed = true);
        }

        void BuildScroll(Node n, GameObject go)
        {
            n.Layout.flexibleWidth = 1f;
            var img = go.AddComponent<Image>(); img.sprite = whiteSprite; img.color = new Color(0, 0, 0, 0.001f);
            var viewport = Child(go, "Viewport", Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-10, 0));
            viewport.AddComponent<RectMask2D>();
            var vpImg = viewport.AddComponent<Image>(); vpImg.sprite = whiteSprite; vpImg.color = new Color(0, 0, 0, 0.001f);
            var content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var crt = (RectTransform)content.transform;
            crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1f);
            crt.offsetMin = crt.offsetMax = Vector2.zero;
            var g = content.AddComponent<VerticalLayoutGroup>();
            Configure(g, new RectOffset(0, 4, 0, 0), 4f);
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var bar = Child(go, "Scrollbar", new Vector2(1, 0), Vector2.one, new Vector2(-6, 0), Vector2.zero);
            var barImg = bar.AddComponent<Image>(); barImg.sprite = whiteSprite; barImg.color = new Color(1, 1, 1, 0.05f);
            var area = Child(bar, "Sliding Area", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var handle = Child(area, "Handle", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var hImg = handle.AddComponent<Image>(); hImg.sprite = whiteSprite; hImg.color = new Color(1, 1, 1, 0.25f);
            var sb = bar.AddComponent<Scrollbar>();
            sb.handleRect = (RectTransform)handle.transform;
            sb.targetGraphic = hImg;
            sb.direction = Scrollbar.Direction.BottomToTop;

            n.Scroll = go.AddComponent<ScrollRect>();
            n.Scroll.viewport = (RectTransform)viewport.transform;
            n.Scroll.content = crt;
            n.Scroll.horizontal = false;
            n.Scroll.vertical = true;
            n.Scroll.movementType = ScrollRect.MovementType.Clamped;
            n.Scroll.scrollSensitivity = 30f;
            n.Scroll.verticalScrollbar = sb;
            n.Scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            n.Child = new Container { Rect = crt, Group = g, Vertical = true };
        }

        static GameObject Child(GameObject parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
            return go;
        }
    }

    /// <summary>Drag a panel around by its background.</summary>
    public class UiDrag : MonoBehaviour, IDragHandler
    {
        public RectTransform Target;

        public void OnDrag(PointerEventData e)
        {
            var canvas = GetComponentInParent<Canvas>();
            float s = canvas != null ? canvas.scaleFactor : 1f;
            Target.anchoredPosition += e.delta / s;
        }
    }
}
