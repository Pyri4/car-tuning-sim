using System;
using Godot;

namespace CarTuningSim.UI;

/// <summary>Small helpers so view code stays readable.</summary>
public static class Ui
{
    public static readonly Color Accent = new(0.91f, 0.64f, 0.24f);
    public static readonly Color Good = new(0.45f, 0.85f, 0.45f);
    public static readonly Color Caution = new(0.95f, 0.80f, 0.30f);
    public static readonly Color Danger = new(0.95f, 0.35f, 0.30f);
    public static readonly Color Muted = new(0.65f, 0.67f, 0.72f);

    public static Label Label(string text, int size = 14, Color? color = null)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c) l.AddThemeColorOverride("font_color", c);
        return l;
    }

    public static Label Heading(string text) => Label(text, 18, Accent);

    public static Label Wrapped(string text, int size = 14, Color? color = null)
    {
        var l = Label(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    public static Button Button(string text, Action onPressed, bool expand = false)
    {
        var b = new Button { Text = text };
        b.Pressed += onPressed;
        if (expand) b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return b;
    }

    public static VBoxContainer VBox(int separation = 6)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", separation);
        return v;
    }

    public static HBoxContainer HBox(int separation = 8)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", separation);
        return h;
    }

    public static MarginContainer Margin(Control child, int margin = 10)
    {
        var m = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) m.AddThemeConstantOverride($"margin_{side}", margin);
        m.AddChild(child);
        return m;
    }

    public static PanelContainer Panel(Control child, int margin = 10)
    {
        var p = new PanelContainer();
        p.AddChild(Margin(child, margin));
        return p;
    }

    public static ScrollContainer Scroll(Control child, bool expand = true)
    {
        var s = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        child.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        s.AddChild(child);
        if (expand) s.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return s;
    }

    public static T Expand<T>(T c, bool horizontal = true, bool vertical = false) where T : Control
    {
        if (horizontal) c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (vertical) c.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return c;
    }

    public static void Clear(Node n)
    {
        foreach (var child in n.GetChildren())
        {
            n.RemoveChild(child);
            child.QueueFree();
        }
    }

    public static Color ConditionColor(double condition) =>
        condition <= 0 ? Danger : condition < 0.4 ? Danger : condition < 0.7 ? Caution : Good;

    /// <summary>Application theme: readable buttons and inputs on the dark background.</summary>
    public static Theme BuildTheme()
    {
        var theme = new Theme();
        StyleBoxFlat Box(Color bg, Color? border = null)
        {
            var sb = new StyleBoxFlat { BgColor = bg };
            sb.SetCornerRadiusAll(4);
            sb.ContentMarginLeft = sb.ContentMarginRight = 10;
            sb.ContentMarginTop = sb.ContentMarginBottom = 4;
            if (border is { } b) { sb.BorderColor = b; sb.SetBorderWidthAll(1); }
            return sb;
        }
        theme.SetStylebox("normal", "Button", Box(new Color(0.24f, 0.26f, 0.31f), new Color(0.34f, 0.37f, 0.44f)));
        theme.SetStylebox("hover", "Button", Box(new Color(0.30f, 0.33f, 0.40f), Accent));
        theme.SetStylebox("pressed", "Button", Box(new Color(0.45f, 0.32f, 0.12f), Accent));
        theme.SetStylebox("disabled", "Button", Box(new Color(0.18f, 0.19f, 0.22f)));
        theme.SetStylebox("focus", "Button", Box(new Color(0, 0, 0, 0), Accent));
        theme.SetColor("font_disabled_color", "Button", new Color(0.45f, 0.47f, 0.52f));
        theme.SetStylebox("normal", "OptionButton", Box(new Color(0.24f, 0.26f, 0.31f), new Color(0.34f, 0.37f, 0.44f)));
        theme.SetStylebox("hover", "OptionButton", Box(new Color(0.30f, 0.33f, 0.40f), Accent));
        theme.SetStylebox("pressed", "OptionButton", Box(new Color(0.30f, 0.33f, 0.40f), Accent));
        theme.SetStylebox("normal", "LineEdit", Box(new Color(0.10f, 0.11f, 0.13f), new Color(0.30f, 0.32f, 0.38f)));
        return theme;
    }

    public static void Message(Node parent, string title, string text)
    {
        var d = new AcceptDialog { Title = title, DialogText = text };
        parent.AddChild(d);
        d.Confirmed += d.QueueFree;
        d.Canceled += d.QueueFree;
        d.PopupCentered(new Vector2I(720, 200));
    }
}
