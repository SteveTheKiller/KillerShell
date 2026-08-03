using System;
using System.Xml.Linq;

namespace KillerShell.Models
{
    /// <summary>
    /// One row of the Event Viewer tab (Shell/EventViewerControl.cs): a single record read from
    /// a classic Windows Event Log (Application, System or Security).
    /// </summary>
    /// <remarks>
    /// Not notifying, unlike ProcessInfo - a process row is edited in place on every refresh
    /// tick so the grid keeps its scroll/selection, but an event log record is immutable the
    /// moment it is read. A reload clears and rebuilds the whole collection instead of mutating
    /// rows, so there is nothing here that ever changes after construction.
    /// </remarks>
    public sealed class EventLogEntryInfo
    {
        /// <summary>Which of the three logs this record came from - shown as its own column so
        /// a row still says where it is from when the "All" source is selected.</summary>
        public string LogName { get; }

        /// <summary>"Critical", "Error", "Warning", "Information" or "Verbose" - read from the
        /// record's numeric Level rather than LevelDisplayName, which throws for any provider
        /// Windows has no manifest for (common on forwarded and legacy-source events).</summary>
        public string Level { get; }

        public DateTime Time { get; }
        public string TimeLabel { get; }

        public string Source { get; }
        public int EventId { get; }
        public string TaskCategory { get; }
        public string Message { get; }

        // Everything below is shown only in the double-click details dialog
        // (Controls/EventDetailsDialog.xaml) - the grid's own columns stop at Message, and these
        // are read the same defensive way (Shell/EventViewerControl.cs SafeXxx helpers) because
        // several of them throw for a provider Windows has no manifest for, same as
        // LevelDisplayName/TaskDisplayName already did before this dialog existed.

        /// <summary>e.g. "Audit Success" / "Audit Failure" - mainly meaningful on the Security log.</summary>
        public string Keywords { get; }

        public string Computer { get; }

        /// <summary>The account name when it can be resolved, otherwise the raw SID, otherwise "-".</summary>
        public string User { get; }

        public string ProcessId { get; }
        public string ThreadId { get; }

        /// <summary>Correlates related events raised by one logical operation. "-" when the
        /// record carries none, which is most of them - ActivityId is opt-in per provider.</summary>
        public string ActivityId { get; }

        /// <summary>The log's own sequence number for this record, distinct from EventId (which
        /// identifies the KIND of event, not this particular occurrence).</summary>
        public string RecordId { get; }

        public string Opcode { get; }

        /// <summary>The record's raw XML (EventRecord.ToXml()), captured eagerly at read time -
        /// the EventRecord itself does not survive past the reader that produced it.</summary>
        public string RawXml { get; }

        /// <summary>RawXml re-serialized with indentation for the details dialog's raw-XML view
        /// (Controls/EventDetailsDialog.xaml.cs) - EventRecord.ToXml() comes back as one
        /// unindented line, unreadable at any font size. Computed once here rather than every
        /// time the dialog's XML view is toggled, since that view can be flipped back and forth
        /// repeatedly and event XML can be reasonably long. Falls back to the raw string on a
        /// malformed record rather than leaving the view blank.</summary>
        public string RawXmlFormatted { get; }

        public EventLogEntryInfo(string logName, string level, DateTime time, string source,
                                  int eventId, string taskCategory, string message,
                                  string keywords, string computer, string user,
                                  string processId, string threadId, string activityId,
                                  string recordId, string opcode, string rawXml)
        {
            LogName      = logName;
            Level        = level;
            Time         = time;
            TimeLabel    = time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            Source       = source;
            EventId      = eventId;
            TaskCategory = taskCategory;
            Message      = message;
            Keywords     = keywords;
            Computer     = computer;
            User         = user;
            ProcessId    = processId;
            ThreadId     = threadId;
            ActivityId   = activityId;
            RecordId     = recordId;
            Opcode       = opcode;
            RawXml       = rawXml;
            RawXmlFormatted = FormatXml(rawXml);
        }

        /// <summary>XDocument.Parse + ToString() is the simplest way to get indented XML back
        /// out, and ToString() indents by default. A record whose XML does not parse (should not
        /// happen in practice, but a bad provider is exactly the kind of thing that already
        /// forced every other field on this type to be read defensively) falls back to the raw,
        /// unformatted string instead of throwing.</summary>
        private static string FormatXml(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            try { return XDocument.Parse(raw).ToString(); }
            catch { return raw; }
        }
    }
}
