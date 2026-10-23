using System;
using System.IO;

// What TerminalControl needs from whatever is on the other side of the screen: a pseudoconsole
// it started itself (ConPty.cs), or a console Windows handed over when a console program was
// launched with KillerShell as the default terminal (HandoffConnection.cs).
namespace KillerShell.Terminal
{
    internal interface ITerminalConnection : IDisposable
    {
        /// <summary>Everything the program draws, as a VT byte stream.</summary>
        Stream Output { get; }

        /// <summary>Keystrokes, VT encoded, in.</summary>
        Stream Input { get; }

        /// <summary>Raised when the program exits, with its exit code. Fires OFF the UI thread.</summary>
        event Action<int>? Exited;

        bool HasExited { get; }

        /// <summary>Tell the program its console changed size.</summary>
        void Resize(short cols, short rows);
    }
}
