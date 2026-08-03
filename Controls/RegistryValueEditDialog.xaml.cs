using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

using KillerShell.Shell;

namespace KillerShell
{
    // ONE dialog whose input control adapts to the value's RegistryValueKind - see the XAML
    // header remark. Reached by double-click, Enter, or "Modify..." on a value row
    // (RegistryEditorControl.cs). Requires an explicit Save click: there is no auto-save on
    // focus-lost anywhere in here, which is the whole point of this dialog existing rather than
    // an inline grid-cell edit - a registry value edit must never write from an accidental Tab.
    public partial class RegistryValueEditDialog : Window
    {
        public bool Confirmed { get; private set; }

        /// <summary>The value to write, already boxed in the exact CLR type
        /// RegistryKey.SetValue expects for this kind (string / string[] / int / long / byte[]).
        /// Only meaningful when Confirmed is true.</summary>
        public object? ResultValue { get; private set; }

        private readonly RegistryValueKind _kind;

        public RegistryValueEditDialog(string displayName, RegistryValueKind kind, object? currentValue)
        {
            InitializeComponent();
            _kind = kind;

            Loaded += (_, _) => Anim.FadeIn(RootBorder);
            SourceInitialized += (_, _) => MainWindow.ApplyThemeBorder(this);

            NameText.Text = displayName;
            KindText.Text = RegistryValueFormat.KindLabel(kind);

            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    StringPanel.Visibility = Visibility.Visible;
                    StringBox.Text = currentValue as string ?? string.Empty;
                    Loaded += (_, _) => { StringBox.Focus(); StringBox.SelectAll(); };
                    break;

                case RegistryValueKind.MultiString:
                    MultiStringPanel.Visibility = Visibility.Visible;
                    MultiStringBox.Text = string.Join(Environment.NewLine, currentValue as string[] ?? Array.Empty<string>());
                    Loaded += (_, _) => { MultiStringBox.Focus(); MultiStringBox.SelectAll(); };
                    break;

                case RegistryValueKind.DWord:
                case RegistryValueKind.QWord:
                    NumberPanel.Visibility = Visibility.Visible;
                    long asLong = System.Convert.ToInt64(currentValue ?? 0L, CultureInfo.InvariantCulture);
                    NumberBox.Text = kind == RegistryValueKind.DWord
                        ? unchecked((uint)asLong).ToString("x", CultureInfo.InvariantCulture)
                        : unchecked((ulong)asLong).ToString("x", CultureInfo.InvariantCulture);
                    Loaded += (_, _) => { NumberBox.Focus(); NumberBox.SelectAll(); };
                    break;

                case RegistryValueKind.Binary:
                default:
                    BinaryPanel.Visibility = Visibility.Visible;
                    var bytes = currentValue as byte[] ?? Array.Empty<byte>();
                    BinaryBox.Text = string.Join(" ", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                    Loaded += (_, _) => { BinaryBox.Focus(); BinaryBox.SelectAll(); };
                    break;
            }
        }

        /// <summary>Re-renders the number box's text when the base radio changes, keeping the same
        /// numeric value - so switching Hex/Decimal never silently changes what will be saved.</summary>
        private void NumberBase_Changed(object sender, RoutedEventArgs e)
        {
            if (NumberBox == null) return;   // fires once during InitializeComponent, before load
            if (!TryParseNumber(out ulong value, out _)) return;
            NumberBox.Text = HexRadio.IsChecked == true
                ? value.ToString("x", CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);
        }

        private bool TryParseNumber(out ulong value, out string error)
        {
            value = 0;
            error = string.Empty;
            string text = NumberBox.Text.Trim();
            var style = HexRadio.IsChecked == true ? NumberStyles.HexNumber : NumberStyles.Integer;
            // The box always holds whichever base is currently selected - NumberBase_Changed
            // rewrites it the moment the radio flips, so there is only ever one interpretation
            // live at a time.
            if (ulong.TryParse(text, style, CultureInfo.InvariantCulture, out value)) return true;
            error = MainWindow.LocStatic("Str_RegEd_InvalidNumber");
            return false;
        }

        private bool TryCommit()
        {
            ErrorText.Visibility = Visibility.Collapsed;

            switch (_kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    ResultValue = StringBox.Text;
                    break;

                case RegistryValueKind.MultiString:
                    // One entry per line - blank trailing lines are trimmed, matching regedit's
                    // own multi-string editor, which never keeps a phantom empty final entry.
                    ResultValue = MultiStringBox.Text
                        .Replace("\r\n", "\n").Split('\n')
                        .Reverse().SkipWhile(string.IsNullOrEmpty).Reverse().ToArray();
                    break;

                case RegistryValueKind.DWord:
                    if (!TryParseNumber(out ulong dw, out string dwErr)) { return Fail(dwErr); }
                    if (dw > uint.MaxValue) return Fail(MainWindow.LocStatic("Str_RegEd_OutOfRangeDword"));
                    ResultValue = unchecked((int)(uint)dw);
                    break;

                case RegistryValueKind.QWord:
                    if (!TryParseNumber(out ulong qw, out string qwErr)) { return Fail(qwErr); }
                    ResultValue = unchecked((long)qw);
                    break;

                case RegistryValueKind.Binary:
                default:
                    if (!TryParseHexBytes(BinaryBox.Text, out byte[] bytes, out string binErr))
                        return Fail(binErr);
                    ResultValue = bytes;
                    break;
            }

            Confirmed = true;
            return true;
        }

        private bool Fail(string message)
        {
            ErrorText.Text       = message;
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }

        /// <summary>Parses whitespace-separated hex byte pairs ("AA BB CC ..."), the same plain
        /// text-of-hex-pairs shape real regedit's own binary editor reads back. Newlines are
        /// treated as whitespace too, since the box wraps.</summary>
        private static bool TryParseHexBytes(string text, out byte[] bytes, out string error)
        {
            error = string.Empty;
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new System.Collections.Generic.List<byte>(parts.Length);
            foreach (var p in parts)
            {
                if (!byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    bytes = Array.Empty<byte>();
                    error = string.Format(MainWindow.LocStatic("Str_RegEd_InvalidByte"), p);
                    return false;
                }
                list.Add(b);
            }
            bytes = list.ToArray();
            return true;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (TryCommit()) Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();
    }
}
