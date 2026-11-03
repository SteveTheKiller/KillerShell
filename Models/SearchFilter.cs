using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

using KillerShell.Shell;

namespace KillerShell.Models
{
    // One filter row: [field] [condition toggle] [value]. The engine ANDs every
    // active filter with the term group - a file must pass all of them to be a result.
    // Name/content matching belongs to SEARCH TERMS, not here.
    public class SearchFilter : INotifyPropertyChanged
    {
        // FieldIndex mirrors the XAML ComboBox item order - keep in sync with MainWindow.xaml.
        public const int FieldExt = 0, FieldDate = 1, FieldSize = 2;

        // ConditionIndex meaning per field (toggled by the condition ModeButton):
        //   Extension: 0 = is,     1 = is not
        //   Date:      0 = before, 1 = after
        //   Size:      0 = over,   1 = under
        public const int UnitKb = 0, UnitMb = 1;

        private int _fieldIndex = FieldExt;
        public int FieldIndex
        {
            get => _fieldIndex;
            set
            {
                if (_fieldIndex == value) return;
                _fieldIndex = value;
                _conditionIndex = 0;   // conditions are per-field; reset on field change
                Notify();
                Notify(nameof(ConditionIndex));
                Notify(nameof(IsExt));
                Notify(nameof(IsDate));
                Notify(nameof(IsSize));
            }
        }

        public bool IsExt  => _fieldIndex == FieldExt;
        public bool IsDate => _fieldIndex == FieldDate;
        public bool IsSize => _fieldIndex == FieldSize;

        private int _conditionIndex;
        public int ConditionIndex
        {
            get => _conditionIndex;
            set { if (value >= 0) { _conditionIndex = value; Notify(); } }
        }

        private string _text = string.Empty;   // extension value, e.g. "log" / ".log" / "log;tmp"
        public string Text
        {
            get => _text;
            set { _text = value; Notify(); }
        }

        private DateTime? _date;
        public DateTime? Date
        {
            get => _date;
            set { _date = value; Notify(); }
        }

        private string _sizeText = string.Empty;
        public string SizeText
        {
            get => _sizeText;
            set { _sizeText = value; Notify(); }
        }

        private int _unitIndex = UnitKb;
        public int UnitIndex
        {
            get => _unitIndex;
            set { if (value >= 0) { _unitIndex = value; Notify(); } }
        }

        // A filter only participates in the search once its value is usable.
        public bool IsActive
        {
            get
            {
                if (IsExt)  return !string.IsNullOrWhiteSpace(_text);
                if (IsDate) return _date.HasValue;
                return SizeBytes > 0;
            }
        }

        public long SizeBytes
        {
            get
            {
                if (!double.TryParse(_sizeText, out double v) || v <= 0) return 0;
                return (long)(v * (_unitIndex == UnitMb ? 1024L * 1024L : 1024L));
            }
        }

        /// <summary>The size text in the form the session stores: invariant, so it reads back
        /// under any region. Text that is not a number is kept as typed.</summary>
        public static string SizeTextToStored(string text) =>
            double.TryParse(text, out double v) ? v.ToString("R", CultureInfo.InvariantCulture) : text;

        /// <summary>Reads a stored size back for the size box. Invariant first; a value saved
        /// before the stored form was invariant (typed under a comma-decimal region) falls back
        /// to the current region. Shown in the current region, as the user would type it.</summary>
        public static string SizeTextFromStored(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            || double.TryParse(text, out v)
                ? v.ToString(CultureInfo.CurrentCulture) : text;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
