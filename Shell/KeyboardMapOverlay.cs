using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace KillerShell.Shell
{
    // ═══════════════════════════════════════════════════════════
    //  VISUAL KEYBOARD  -  the other half of the shortcuts card
    // ═══════════════════════════════════════════════════════════
    // Ported from KillerPDF's KeyboardMapOverlay. Same philosophy as ShortcutsOverlay.cs: the
    // board is generated from the tables below, and every brush and label is wired with
    // SetResourceReference so a theme or language switch repaints it live.
    //
    // Category colors are the KsCat* theme brushes shared with the list, so a key's border tells
    // you which group it belongs to. Keycap faces ride PaneBrush / CardBorderBrush / TextBrush,
    // so the board always looks native to the active theme rather than being a pasted-in image.
    //
    // Holding a real Ctrl / Shift / Alt previews that layer, so you can find a chord by pressing
    // the modifier rather than by reading.
    public partial class MainWindow
    {
        private enum KbLayer { Base, Ctrl, CtrlShift, Shift, Alt }

        private KbLayer _kbLayer = KbLayer.Base;
        private bool _kbBuilt;
        private TextBlock? _kbDetail;
        private TextBlock? _kbHoverAct;   // caption of the key under the mouse (marquee restart on layer switch)
        private string? _kbHoverId;
        private readonly Dictionary<string, (Border Cap, TextBlock Act, Rectangle Bar)> _kbKeys = new();
        private readonly Dictionary<KbLayer, Button> _kbLayerBtns = new();

        private const string KsViewSetting = "ShortcutView";   // "list" (default) | "keyboard"

        // ── Bindings ───────────────────────────────────────────────────────────────────────────
        // key id -> (category, localized label resource key). Categories match ShortcutsOverlay's
        // KsRows, so the two views group the same bindings the same way.
        private static readonly Dictionary<KbLayer, Dictionary<string, (string Cat, string Label)>> KbMap = new()
        {
            [KbLayer.Base] = new()
            {
                ["F1"]    = ("Help",   "Str_Ks_Help"),
                ["F4"]    = ("Nav",    "Str_Ks_Address"),
                ["F5"]    = ("View",   "Str_Ks_Refresh"),
                ["F8"]    = ("Tabs",   "Str_Ks_Shell"),
                ["F9"]    = ("Tabs",   "Str_Ks_TaskManager"),
                ["F11"]   = ("Tabs",   "Str_Ks_Performance"),
                ["F2"]    = ("Edit",   "Str_Ks_Rename"),
                ["F7"]    = ("Search", "Str_Ks_AddFilter"),
                ["Del"]   = ("Edit",   "Str_Ks_Recycle"),
                ["F10"]   = ("View",   "Str_TT_DualPane"),
                ["F12"]   = ("Help",   "Str_Ks_About"),
                ["Enter"] = ("Search", "Str_Ks_Run"),
                ["Esc"]   = ("Edit",   "Str_Ks_Esc"),
                ["Back"]  = ("Nav",    "Str_Ks_Back"),
            },
            [KbLayer.Ctrl] = new()
            {
                ["N"]     = ("Tabs",   "Str_Ks_NewWindow"),
                ["F"]     = ("Search", "Str_Ks_FilterResults"),
                ["L"]     = ("Nav",    "Str_Ks_Address"),
                ["O"]     = ("Nav",    "Str_Ks_Folder"),
                ["B"]     = ("Nav",    "Str_Ks_Bookmarks"),
                ["H"]     = ("View",   "Str_TT_ShowHidden"),
                ["T"]     = ("Tabs",   "Str_Ks_NewTab"),
                ["W"]     = ("Tabs",   "Str_Ks_CloseTab"),
                ["Tab"]   = ("Tabs",   "Str_Ks_NextTab"),
                ["E"]     = ("Search", "Str_Ks_FocusSearch"),
                ["A"]     = ("Edit",   "Str_Ks_SelectAll"),
                ["C"]     = ("Edit",   "Str_Ks_CutCopyPaste"),
                ["X"]     = ("Edit",   "Str_Ks_CutCopyPaste"),
                ["V"]     = ("Edit",   "Str_Ks_CutCopyPaste"),
                ["Right"] = ("View",   "Str_Ks_ExpandAll"),
                ["Left"]  = ("View",   "Str_Ks_CollapseAll"),
                ["D1"]    = ("Tabs",   "Str_Ks_JumpTab"), ["D2"] = ("Tabs", "Str_Ks_JumpTab"),
                ["D3"]    = ("Tabs",   "Str_Ks_JumpTab"), ["D4"] = ("Tabs", "Str_Ks_JumpTab"),
                ["D5"]    = ("Tabs",   "Str_Ks_JumpTab"), ["D6"] = ("Tabs", "Str_Ks_JumpTab"),
                ["D7"]    = ("Tabs",   "Str_Ks_JumpTab"), ["D8"] = ("Tabs", "Str_Ks_JumpTab"),
                ["D9"]    = ("Tabs",   "Str_Ks_JumpTab"),
                ["F8"]    = ("Tabs",   "Str_Ks_ShellAdmin"),
                ["F9"]    = ("Tabs",   "Str_Ks_TaskManagerAdmin"),
                ["Grave"] = ("Tabs",   "Str_Ks_Shell"),
                ["F10"]   = ("View",   "Str_Ks_MenuBar"),
                // F12 is lit here, in the Ctrl layer ONLY - the Base layer's F12 (above) stays
                // About and is unaffected. There is no unelevated Event Viewer to show anywhere
                // else on the board.
                ["F12"]   = ("Tabs",   "Str_Ks_EventViewer"),
                // Same shape for F11: the Base layer's F11 (above) stays Performance, and this
                // Ctrl layer entry is the ONLY place Registry Editor is lit - there is no
                // unelevated variant to show anywhere else on the board.
                ["F11"]   = ("Tabs",   "Str_Ks_RegistryEditor"),
            },
            [KbLayer.CtrlShift] = new()
            {
                ["N"]   = ("Edit",   "Str_Ks_NewFolder"),
                ["A"]   = ("Search", "Str_Ks_AddTerm"),
                ["C"]   = ("Search", "Str_Ks_CaseSensitive"),
                ["F"]   = ("Search", "Str_Ks_Pipe"),
                ["S"]   = ("View",   "Str_Ks_SearchPanel"),
                ["L"]   = ("Edit",   "Str_Ks_Clear"),
                ["Tab"] = ("Tabs",   "Str_Ks_NextTab"),
                ["F8"]  = ("Tabs",   "Str_Ks_ShellCmdAdmin"),
            },
            [KbLayer.Shift] = new()
            {
                ["Del"] = ("Edit", "Str_Ks_DeleteForever"),
                ["F8"]  = ("Tabs", "Str_Ks_ShellCmd"),
                ["F10"] = ("File", "Str_Menu_ShellMenu"),
            },
            [KbLayer.Alt] = new()
            {
                ["D"]     = ("Nav", "Str_Ks_Address"),
                ["Left"]  = ("Nav", "Str_Ks_BackForward"),
                ["Right"] = ("Nav", "Str_Ks_BackForward"),
                ["Up"]    = ("Nav", "Str_Ks_Up"),
                ["D1"] = ("Nav", "Str_Ks_JumpBookmark"), ["D2"] = ("Nav", "Str_Ks_JumpBookmark"),
                ["D3"] = ("Nav", "Str_Ks_JumpBookmark"), ["D4"] = ("Nav", "Str_Ks_JumpBookmark"),
                ["D5"] = ("Nav", "Str_Ks_JumpBookmark"), ["D6"] = ("Nav", "Str_Ks_JumpBookmark"),
                ["D7"] = ("Nav", "Str_Ks_JumpBookmark"), ["D8"] = ("Nav", "Str_Ks_JumpBookmark"),
                ["D9"] = ("Nav", "Str_Ks_JumpBookmark"), ["D0"] = ("Nav", "Str_Ks_JumpBookmark"),
                ["P"]  = ("View", "Str_TT_DetailsPane"),
            },
        };

        // ── Physical layout ────────────────────────────────────────────────────────────────────
        // (id, cap text, width units). id "" = spacer. Numpad omitted - the digits mirror the
        // number row and it would double the board's width for nothing.
        private static readonly (string Id, string Cap, double W)[][] KbRows =
        [
            [("Esc","Esc",1), ("","",0.8), ("F1","F1",1),("F2","F2",1),("F3","F3",1),("F4","F4",1), ("","",0.6),
             ("F5","F5",1),("F6","F6",1),("F7","F7",1),("F8","F8",1), ("","",0.6),
             ("F9","F9",1),("F10","F10",1),("F11","F11",1),("F12","F12",1)],
            [("Grave","`",1),("D1","1",1),("D2","2",1),("D3","3",1),("D4","4",1),("D5","5",1),("D6","6",1),
             ("D7","7",1),("D8","8",1),("D9","9",1),("D0","0",1),("Minus","-",1),("Equals","=",1),("Back","⌫",2),
             ("","",0.6), ("Ins","Ins",1),("Home","Home",1),("PgUp","PgUp",1)],
            [("Tab","Tab",1.5),("Q","Q",1),("W","W",1),("E","E",1),("R","R",1),("T","T",1),("Y","Y",1),("U","U",1),
             ("I","I",1),("O","O",1),("P","P",1),("LBr","[",1),("RBr","]",1),("Bslash","\\",1.5),
             ("","",0.6), ("Del","Del",1),("End","End",1),("PgDn","PgDn",1)],
            [("Caps","Caps",1.8),("A","A",1),("S","S",1),("D","D",1),("F","F",1),("G","G",1),("H","H",1),("J","J",1),
             ("K","K",1),("L","L",1),("Semi",";",1),("Quote","'",1),("Enter","Enter",2.2)],
            [("Shift","Shift",2.3),("Z","Z",1),("X","X",1),("C","C",1),("V","V",1),("B","B",1),("N","N",1),("M","M",1),
             ("Comma",",",1),("Period",".",1),("Slash","/",1),("RShift","Shift",2.7),
             ("","",1.6), ("Up","↑",1)],
            [("Ctrl","Ctrl",1.5),("Win","Win",1.2),("Alt","Alt",1.5),("Space","",6.8),("RAlt","Alt",1.5),("Menu","☰",1),("RCtrl","Ctrl",1.5),
             ("","",0.6), ("Left","←",1),("Down","↓",1),("Right","→",1)],
        ];

        private static readonly (KbLayer Layer, string Caption)[] KbLayerButtons =
        [
            (KbLayer.Base, "BASE"), (KbLayer.Ctrl, "CTRL"), (KbLayer.CtrlShift, "CTRL+SHIFT"),
            (KbLayer.Shift, "SHIFT"), (KbLayer.Alt, "ALT"),
        ];

        // Modifier keycaps that light up per layer - they define the layer rather than carry a
        // binding of their own.
        private static readonly Dictionary<KbLayer, string[]> KbLayerMods = new()
        {
            [KbLayer.Base] = [], [KbLayer.Ctrl] = ["Ctrl", "RCtrl"],
            [KbLayer.CtrlShift] = ["Ctrl", "RCtrl", "Shift", "RShift"],
            [KbLayer.Shift] = ["Shift", "RShift"], [KbLayer.Alt] = ["Alt", "RAlt"],
        };

        // ── View toggle (LIST / KEYBOARD) ──────────────────────────────────────────────────────

        private void KsViewList_Click(object sender, RoutedEventArgs e)     => ApplyShortcutView(keyboard: false, persist: true);
        private void KsViewKeyboard_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(keyboard: true,  persist: true);

        /// <summary>
        /// Shows the list or the keyboard inside the shortcuts card. Called on every open with
        /// the remembered choice, and by the two toggle captions.
        /// </summary>
        private void ApplyShortcutView(bool keyboard, bool persist = false)
        {
            BuildShortcutsList();                       // ShortcutsOverlay.cs - no-op after the first
            if (keyboard && !_kbBuilt) BuildKeyboardView();

            ShortcutListHost.Visibility     = keyboard ? Visibility.Collapsed : Visibility.Visible;
            ShortcutKeyboardHost.Visibility = keyboard ? Visibility.Visible : Visibility.Collapsed;

            // Each view sets its own width: a board's worth for the keyboard, two columns' worth
            // for the list. Neither is the other's size.
            ShortcutCard.Width = keyboard ? 1000 : 780;

            // Height comes from the WINDOW, not from the content. The card is centered and has
            // no height of its own, so a list taller than the window was simply clipped and the
            // last group lost rows with nothing to say so. 150 covers the card's own chrome:
            // title row, the LIST / KEYBOARD toggle, the hint line and the margins.
            ShortcutScroll.MaxHeight = Math.Max(200, ActualHeight - 150);

            KsViewListBtn.SetResourceReference(ForegroundProperty,     keyboard ? "MutedTextBrush" : "PrimaryBrush");
            KsViewKeyboardBtn.SetResourceReference(ForegroundProperty, keyboard ? "PrimaryBrush" : "MutedTextBrush");

            if (keyboard) SetKbLayer(KbLayer.Base);
            if (persist) Services.ThemeManager.SetSetting(KsViewSetting, keyboard ? "keyboard" : "list");
        }

        private void ApplyPersistedShortcutView() =>
            ApplyShortcutView(Services.ThemeManager.GetSetting(KsViewSetting) == "keyboard");

        // ── Board construction (once, lazily) ──────────────────────────────────────────────────

        private void BuildKeyboardView()
        {
            _kbBuilt = true;
            var host = ShortcutKeyboardHost;
            host.Children.Clear();
            _kbKeys.Clear();
            _kbLayerBtns.Clear();

            // Layer captions row.
            var layerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            foreach (var (layer, caption) in KbLayerButtons)
            {
                var b = new Button
                {
                    Content = caption,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(0, 0, 8, 0),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    FocusVisualStyle = null,
                    Template = BareButtonTemplate(),   // no stock WPF hover chrome
                };
                b.SetResourceReference(BackgroundProperty, "PaneBrush");
                b.SetResourceReference(ForegroundProperty, "MutedTextBrush");
                b.SetResourceReference(BorderBrushProperty, "CardBorderBrush");
                var l = layer;
                b.Click += (_, _2) => SetKbLayer(l);
                _kbLayerBtns[layer] = b;
                layerRow.Children.Add(b);
            }
            var hint = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            hint.SetResourceReference(TextBlock.TextProperty, "Str_Ks_HoldHint");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            layerRow.Children.Add(hint);
            host.Children.Add(layerRow);

            // The board itself. A DownOnly Viewbox keeps it fitting a small window without
            // introducing a scrollbar across a picture of a keyboard.
            const double U = 46;   // one key unit including its 4px gap
            var board = new StackPanel();
            foreach (var row in KbRows)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                foreach (var (id, cap, w) in row)
                {
                    if (id.Length == 0) { r.Children.Add(new Border { Width = U * w }); continue; }

                    var capText = new TextBlock
                    {
                        Text = cap,
                        FontFamily = new FontFamily("Consolas"),   // symbols come from font fallback
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 5, 0, 0),
                    };
                    capText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

                    var act = new TextBlock
                    {
                        FontSize = 8.5,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Visibility = Visibility.Collapsed,
                        RenderTransform = new TranslateTransform(),
                    };
                    var actHost = new Border   // clips the caption so it can marquee on hover
                    {
                        ClipToBounds = true,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(2, 0, 2, 5),
                        Child = act,
                    };
                    var bar = new Rectangle
                    {
                        Height = 3, VerticalAlignment = VerticalAlignment.Bottom,
                        RadiusX = 1.5, RadiusY = 1.5,
                        Margin = new Thickness(3, 0, 3, 0),
                        Visibility = Visibility.Collapsed,
                    };

                    var inner = new Grid();
                    inner.Children.Add(capText);
                    inner.Children.Add(actHost);
                    inner.Children.Add(bar);

                    var key = new Border
                    {
                        Width = U * w - 4, Height = 44,
                        CornerRadius = new CornerRadius(4),
                        BorderThickness = new Thickness(1),
                        Margin = new Thickness(0, 0, 4, 0),
                        Child = inner,
                    };
                    key.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
                    key.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

                    // Hover lifts the cap a few pixels, like the cards on killertools.net.
                    var lift = new TranslateTransform();
                    key.RenderTransform = lift;
                    string keyId = id;
                    key.MouseEnter += (_, _2) =>
                    {
                        _kbHoverAct = act; _kbHoverId = keyId;
                        KbShowDetail(keyId);
                        if (KbMap[_kbLayer].ContainsKey(keyId))   // only bound keys lift; the rest stay put
                        {
                            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(90)));
                            KbMarqueeStart(act);
                        }
                    };
                    key.MouseLeave += (_, _2) =>
                    {
                        _kbHoverAct = null; _kbHoverId = null;
                        if (_kbDetail is not null) _kbDetail.Text = " ";
                        lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(130)));
                        KbMarqueeStop(act);
                    };

                    _kbKeys[id] = (key, act, bar);
                    r.Children.Add(key);
                }
                board.Children.Add(r);
            }
            host.Children.Add(new Viewbox
            {
                Child = board,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            _kbDetail = new TextBlock
            {
                Text = " ",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                Margin = new Thickness(2, 10, 0, 0),
                Height = 18,
            };
            _kbDetail.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
            host.Children.Add(_kbDetail);
        }

        /// <summary>A Border + ContentPresenter, so a layer button carries no stock WPF chrome
        /// and its local Foreground / BorderBrush actually show.</summary>
        private static ControlTemplate BareButtonTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty,      new Binding("Background")      { RelativeSource = RelativeSource.TemplatedParent });
            b.SetBinding(Border.BorderBrushProperty,     new Binding("BorderBrush")     { RelativeSource = RelativeSource.TemplatedParent });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
            b.SetBinding(Border.PaddingProperty,         new Binding("Padding")         { RelativeSource = RelativeSource.TemplatedParent });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            b.AppendChild(cp);

            return new ControlTemplate(typeof(Button)) { VisualTree = b };
        }

        private void KbShowDetail(string id)
        {
            if (_kbDetail is null) return;
            if (KbMap[_kbLayer].TryGetValue(id, out var b))
            {
                string section = TryFindResource(KsCatLabelKey(b.Cat)) as string ?? b.Cat;
                string label   = TryFindResource(b.Label) as string ?? b.Label;
                _kbDetail.Text = section + " :: " + label;
            }
            else _kbDetail.Text = " ";
        }

        // ── Caption marquee (hover a lit key whose caption is cut off) ─────────────────────────

        /// <summary>Scrolls a truncated caption back and forth inside its clipped host while the
        /// key is hovered. No-op when the full text already fits.</summary>
        private void KbMarqueeStart(TextBlock act)
        {
            if (act.Visibility != Visibility.Visible || act.Parent is not Border host) return;

            // Measure with a probe TextBlock, NOT FormattedText: the probe inherits the same text
            // formatting mode as the live control, so its width matches what actually renders.
            // FormattedText measures Ideal-mode metrics and under-reports by a couple of pixels,
            // which leaves barely-trimmed captions never scrolling.
            var probe = new TextBlock
            {
                Text = act.Text, FontFamily = act.FontFamily, FontSize = act.FontSize,
                FontStyle = act.FontStyle, FontWeight = act.FontWeight, FontStretch = act.FontStretch,
            };
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(act));
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double over = probe.DesiredSize.Width - host.ActualWidth;
            if (over <= 0.5) return;

            // Reparent the caption into a Canvas for the ride. A Canvas measures children with
            // INFINITE space, so the TextBlock escapes WPF's layout clip and renders the whole
            // caption; the host border clips the viewport. Arranged directly in the too-small
            // host, the TextBlock is clipped to its slot BEFORE the transform runs, so the
            // animation would just slide a pre-cut snapshot.
            double h = act.ActualHeight;
            act.TextTrimming = TextTrimming.None;
            host.Child = null;
            var cv = new Canvas { Height = h };
            cv.Children.Add(act);
            Canvas.SetLeft(act, 0);
            Canvas.SetTop(act, 0);
            host.Child = cv;

            var tt = (TranslateTransform)act.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -over, TimeSpan.FromMilliseconds(Math.Max(600, over * 40)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(350) });
        }

        private void KbMarqueeStop(TextBlock act)
        {
            var tt = (TranslateTransform)act.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
            act.TextTrimming = TextTrimming.CharacterEllipsis;
            if (act.Parent is Canvas cv && cv.Parent is Border host)
            {
                cv.Children.Clear();
                host.Child = act;   // back to the plain centered, ellipsized layout
            }
        }

        // ── Layer painting ─────────────────────────────────────────────────────────────────────

        private void SetKbLayer(KbLayer layer)
        {
            _kbLayer = layer;
            if (!_kbBuilt) return;

            var map = KbMap[layer];
            foreach (var kv in _kbKeys)   // no KeyValuePair deconstruction on net48
            {
                var vis = kv.Value;
                if (map.TryGetValue(kv.Key, out var b))
                {
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty, "KsCat" + b.Cat);
                    vis.Bar.SetResourceReference(Shape.FillProperty, "KsCat" + b.Cat);
                    vis.Bar.Visibility = Visibility.Visible;
                    vis.Act.SetResourceReference(TextBlock.TextProperty, b.Label);
                    vis.Act.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + b.Cat);
                    vis.Act.Visibility = Visibility.Visible;
                }
                else
                {
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    vis.Bar.Visibility = Visibility.Collapsed;
                    vis.Act.Visibility = Visibility.Collapsed;
                }
            }

            // Modifier caps that define the layer take the accent; the layer captions follow.
            string[] allMods = ["Ctrl", "RCtrl", "Shift", "RShift", "Alt", "RAlt"];
            foreach (var m in allMods)
                if (_kbKeys.TryGetValue(m, out var vis))
                    vis.Cap.SetResourceReference(Border.BorderBrushProperty,
                        Array.IndexOf(KbLayerMods[layer], m) >= 0 ? "PrimaryBrush" : "CardBorderBrush");

            foreach (var kv in _kbLayerBtns)   // no KeyValuePair deconstruction on net48
            {
                kv.Value.SetResourceReference(ForegroundProperty,  kv.Key == layer ? "PrimaryBrush" : "MutedTextBrush");
                kv.Value.SetResourceReference(BorderBrushProperty, kv.Key == layer ? "PrimaryBrush" : "CardBorderBrush");
            }

            // Layer changed while a key is hovered (holding Ctrl / Shift / Alt): restart that
            // key's marquee for its NEW caption - MouseEnter alone never re-fires. Deferred one
            // layout pass so the caption text and size are current before measuring.
            if (_kbHoverAct is not null && _kbHoverId is not null)
            {
                KbMarqueeStop(_kbHoverAct);
                KbShowDetail(_kbHoverId);
                var act = _kbHoverAct;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(act, _kbHoverAct)) KbMarqueeStart(act);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Maps the live modifier state to a layer while the keyboard view is showing - called
        /// from the window's key handlers, so holding Ctrl / Shift / Alt previews that layer.
        /// </summary>
        private void KbSyncLayerFromModifiers()
        {
            if (!_kbBuilt || ShortcutKeyboardHost.Visibility != Visibility.Visible) return;

            var m = Keyboard.Modifiers;
            var layer = m.HasFlag(ModifierKeys.Control) && m.HasFlag(ModifierKeys.Shift) ? KbLayer.CtrlShift
                      : m.HasFlag(ModifierKeys.Control) ? KbLayer.Ctrl
                      : m.HasFlag(ModifierKeys.Alt) ? KbLayer.Alt
                      : m.HasFlag(ModifierKeys.Shift) ? KbLayer.Shift
                      : KbLayer.Base;
            if (layer != _kbLayer) SetKbLayer(layer);
        }
    }
}
