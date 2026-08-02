using System;
using System.ComponentModel;
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

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
