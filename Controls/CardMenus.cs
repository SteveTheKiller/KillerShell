using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace KillerShell.Controls
{
    /// <summary>
    /// The kit's card menu pieces (Controls.xaml CardMenu, CardSlot, CardQuickButton), put together
    /// in code for the menus that are built rather than declared: the tab card and the terminal card.
    /// The file card (FilePane.xaml ResultMenu) declares the same pieces in XAML.
    /// </summary>
    internal static class CardMenus
    {
        /// <summary>An empty card: the kit's CardMenu style on a fresh ContextMenu.</summary>
        internal static ContextMenu Card()
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
            // A quick strip that ends the card drops its bottom rule: the card's own edge is right
            // under it, and two lines there read as a stray divider. Checked on every opening,
            // since a card can gain or hide rows between openings.
            menu.Opened += (_, _) =>
            {
                var last = menu.Items.OfType<FrameworkElement>().LastOrDefault(item => item.Visibility == Visibility.Visible);
                foreach (var slot in menu.Items.OfType<MenuItem>())
                    if (slot.Tag as string == "cardquick" && slot.Header is Border rule)
                        rule.BorderThickness = new Thickness(0, 1, 0, ReferenceEquals(slot, last) ? 0 : 1);
            };
            return menu;
        }

        /// <summary>A slot row: a MenuItem that only hosts its header and is never highlighted.</summary>
        internal static MenuItem Slot()
        {
            var slot = new MenuItem();
            slot.SetResourceReference(FrameworkElement.StyleProperty, "CardSlot");
            return slot;
        }

        /// <summary>
        /// The card's header: an icon, the name of what the card is about in bold, and one dim line
        /// under it. The name is set on the element for the caller to put on its slot, because a
        /// panel has no automation peer of its own.
        /// </summary>
        internal static FrameworkElement Header(FrameworkElement? icon, string title, string meta)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            text.Children.Add(name);
            if (!string.IsNullOrEmpty(meta))
            {
                var detail = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                detail.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
                text.Children.Add(detail);
            }

            var header = new DockPanel { Margin = new Thickness(4, 2, 4, 6) };
            if (icon != null)
            {
                // Added to, not replaced: a fallback dot centers itself in a full-size box with
                // its own margin, and losing that would pull its text 16 px left of an image's.
                var own = icon.Margin;
                icon.Margin = new Thickness(own.Left, own.Top, own.Right + 10, own.Bottom);
                icon.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(icon, Dock.Left);
                header.Children.Add(icon);
            }
            header.Children.Add(text);
            AutomationProperties.SetName(header, title);
            return header;
        }

        /// <summary>
        /// Put a control at the top right of a card header (the folder star). Docked before the text,
        /// which fills what is left, so a long name trims instead of running under it.
        /// </summary>
        internal static void AddTrailing(FrameworkElement header, FrameworkElement element)
        {
            if (header is not DockPanel dock) return;
            DockPanel.SetDock(element, Dock.Right);
            dock.Children.Insert(0, element);
        }

        /// <summary>Fill a header slot, naming the slot itself so a screen reader hears it.</summary>
        internal static void SetHeader(MenuItem slot, FrameworkElement? icon, string title, string meta)
        {
            slot.Header = Header(icon, title, meta);
            AutomationProperties.SetName(slot, title);
        }

        /// <summary>
        /// The quick-button strip: a slot holding a ruled row the buttons share evenly. A collapsed
        /// button gives its share to the others (UniformGrid counts only the visible ones).
        /// </summary>
        internal static MenuItem QuickStrip(out UniformGrid strip)
        {
            var slot = Slot();
            slot.Padding = new Thickness(0);
            slot.SetResourceReference(AutomationProperties.NameProperty, "Str_Card_Actions");
            strip = new UniformGrid { Rows = 1 };
            var rule = new Border { BorderThickness = new Thickness(0, 1, 0, 1), Padding = new Thickness(2, 4, 2, 4), Child = strip };
            rule.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            slot.Header = rule;
            return slot;
        }

        /// <summary>
        /// One quick button: a glyph over a short caption, the full command and its shortcut in the
        /// tooltip and the automation name. A click closes the card first, then runs the command.
        /// </summary>
        internal static Button QuickButton(UniformGrid strip, ContextMenu card, int glyph, string captionKey,
                                           string commandKey, string? gesture, Action run)
        {
            var button = new Button { Content = ((char)glyph).ToString() };
            button.SetResourceReference(FrameworkElement.StyleProperty, "CardQuickButton");
            button.SetResourceReference(FrameworkElement.TagProperty, captionKey);
            button.SetResourceReference(AutomationProperties.NameProperty, commandKey);

            var tip = new TextBlock();
            var command = new Run();
            command.SetResourceReference(Run.TextProperty, commandKey);
            tip.Inlines.Add(command);
            if (!string.IsNullOrEmpty(gesture)) tip.Inlines.Add(new Run("  (" + gesture + ")"));
            button.ToolTip = tip;

            button.Click += (_, e) =>
            {
                e.Handled = true;
                card.IsOpen = false;
                run();
            };
            strip.Children.Add(button);
            return button;
        }
    }
}
