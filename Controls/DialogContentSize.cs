using System.Windows;
using System.Windows.Controls;

namespace KillerShell
{
    // Shared by EventDetailsDialog, ProcessDetailsDialog and ServiceDetailsDialog: the three
    // details dialogs that open sized to their content and are then resizable by hand.
    //
    // THE BUG THIS FIXES. Those dialogs want two things that fight each other. SizeToContent
    // "Height" needs the body in an AUTO row, because a Star row measured against the infinite
    // height SizeToContent hands down collapses to zero. But an Auto row also measures its child
    // against infinity, so the ScrollViewer in it is always given exactly the height it asked for
    // and its scrollbar can never appear. As long as the window is sized to content that is
    // invisible, because the window is exactly as tall as the content anyway. The moment the
    // height is capped - MaxHeight clamping a long record, or the user dragging the frame, which
    // makes WPF drop SizeToContent - the body keeps its full desired height, overflows the window,
    // and gets clipped at the edge with no scrollbar and no way to reach the rest of it.
    //
    // THE FIX. Sized to content is only wanted for the OPENING size. Once the window has been laid
    // out and rendered once, that job is done, so SizeToContent is released and the body row
    // becomes Star. Nothing moves on screen: the row is already exactly the height Star resolves to
    // at that instant. From then on the dialog is an ordinary resizable window, the body takes
    // whatever is left over, and the ScrollViewer inside it scrolls like any other.
    //
    // One helper rather than the same handler pasted into three code-behinds, for the same reason
    // DialogScreenClamp.cs exists beside it.
    internal static class DialogContentSize
    {
        /// <summary>
        /// Opens sized to content, then hands the dialog over to normal resizing with
        /// <paramref name="bodyRow"/> absorbing the slack. Call from the constructor; the work
        /// happens on the first ContentRendered and unhooks itself.
        /// </summary>
        /// <remarks>
        /// ContentRendered, not Loaded: Loaded runs before the window's content-driven height has
        /// actually been applied, so releasing SizeToContent there can freeze the dialog at the
        /// wrong height. ContentRendered fires after the first frame is on screen, by which point
        /// the height being kept is the one the user is looking at.
        /// </remarks>
        public static void ReleaseAfterFirstRender(Window dialog, RowDefinition bodyRow)
        {
            if (dialog == null || bodyRow == null) return;

            void OnRendered(object? sender, System.EventArgs e)
            {
                dialog.ContentRendered -= OnRendered;

                dialog.SizeToContent = SizeToContent.Manual;
                bodyRow.Height = new GridLength(1, GridUnitType.Star);
            }

            dialog.ContentRendered += OnRendered;
        }
    }
}
