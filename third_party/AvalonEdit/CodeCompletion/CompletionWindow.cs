// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace ICSharpCode.AvalonEdit.CodeCompletion
{
	/// <summary>
	/// The code completion window.
	/// </summary>
	public class CompletionWindow : CompletionWindowBase
	{
		// Dropped on close, and tested for null everywhere it is touched afterwards.
		private ToolTip? toolTip = new();

		/// <summary>
		/// Gets the completion list used in this completion window.
		/// </summary>
		public CompletionList CompletionList { get; } = new();

		/// <summary>
		/// Creates a new code completion window.
		/// </summary>
		public CompletionWindow(TextArea textArea) : base(textArea)
		{
			// keep height automatic
			this.CloseAutomatically = true;
			this.SizeToContent = SizeToContent.Height;
			this.MaxHeight = 300;
			this.Width = 175;
			this.Content = CompletionList;
			// prevent user from resizing window to 0x0
			this.MinHeight = 15;
			this.MinWidth = 30;

			toolTip.PlacementTarget = this;
			toolTip.Placement = PlacementMode.Right;
			toolTip.Closed += toolTip_Closed;

			AttachEvents();
		}

		#region ToolTip handling
		private void toolTip_Closed(object sender, RoutedEventArgs e)
		{
			// Clear content after tooltip is closed.
			// We cannot clear is immediately when setting IsOpen=false
			// because the tooltip uses an animation for closing.
			if (toolTip != null) {
				toolTip.Content = null;
			}
		}

		private void completionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			ICompletionData? item = CompletionList.SelectedItem;
			if (item == null) {
				return;
			}

			// toolTip is dropped in OnClosed, so a selection change arriving after the window has
			// closed would have thrown on every line below this.
			if (toolTip == null) {
				return;
			}

			object description = item.Description;
			if (description != null) {
				if (description is string descriptionText) {
					toolTip.Content = new TextBlock {
						Text = descriptionText,
						TextWrapping = TextWrapping.Wrap
					};
				} else {
					toolTip.Content = description;
				}
				toolTip.IsOpen = true;
			} else {
				toolTip.IsOpen = false;
			}
		}
		#endregion

		private void completionList_InsertionRequested(object sender, EventArgs e)
		{
			Close();
			// The window must close before Complete() is called.
			// If the Complete callback pushes stacked input handlers, we don't want to pop those when the CC window closes.
			ICompletionData? item = CompletionList.SelectedItem;
			item?.Complete(this.TextArea, new AnchorSegment(this.TextArea.Document, this.StartOffset, this.EndOffset - this.StartOffset), e);
		}

		private void AttachEvents()
		{
			this.CompletionList.InsertionRequested += completionList_InsertionRequested;
			this.CompletionList.SelectionChanged += completionList_SelectionChanged;
			this.TextArea.Caret.PositionChanged += CaretPositionChanged;
			this.TextArea.MouseWheel += textArea_MouseWheel;
			this.TextArea.PreviewTextInput += textArea_PreviewTextInput;
		}

		/// <inheritdoc/>
		protected override void DetachEvents()
		{
			this.CompletionList.InsertionRequested -= completionList_InsertionRequested;
			this.CompletionList.SelectionChanged -= completionList_SelectionChanged;
			this.TextArea.Caret.PositionChanged -= CaretPositionChanged;
			this.TextArea.MouseWheel -= textArea_MouseWheel;
			this.TextArea.PreviewTextInput -= textArea_PreviewTextInput;
			base.DetachEvents();
		}

		/// <inheritdoc/>
		protected override void OnClosed(EventArgs e)
		{
			base.OnClosed(e);
			if (toolTip != null) {
				toolTip.IsOpen = false;
				toolTip = null;
			}
		}

		/// <inheritdoc/>
		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!e.Handled) {
				CompletionList.HandleKey(e);
			}
		}

		private void textArea_PreviewTextInput(object sender, TextCompositionEventArgs e)
		{
			e.Handled = RaiseEventPair(this, PreviewTextInputEvent, TextInputEvent,
									   new TextCompositionEventArgs(e.Device, e.TextComposition));
		}

		private void textArea_MouseWheel(object sender, MouseWheelEventArgs e)
		{
			e.Handled = RaiseEventPair(GetScrollEventTarget(),
									   PreviewMouseWheelEvent, MouseWheelEvent,
									   new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta));
		}

		private UIElement GetScrollEventTarget()
		{
			if (CompletionList == null) {
				return this;
			}

			return CompletionList.ScrollViewer ?? CompletionList.ListBox ?? (UIElement)CompletionList;
		}

		/// <summary>
		/// Gets/Sets whether the completion window should close automatically.
		/// The default value is true.
		/// </summary>
		public bool CloseAutomatically { get; set; }

		/// <inheritdoc/>
		protected override bool CloseOnFocusLost => this.CloseAutomatically;

		/// <summary>
		/// When this flag is set, code completion closes if the caret moves to the
		/// beginning of the allowed range. This is useful in Ctrl+Space and "complete when typing",
		/// but not in dot-completion.
		/// Has no effect if CloseAutomatically is false.
		/// </summary>
		public bool CloseWhenCaretAtBeginning { get; set; }

		private void CaretPositionChanged(object sender, EventArgs e)
		{
			int offset = this.TextArea.Caret.Offset;
			if (offset == this.StartOffset) {
				if (CloseAutomatically && CloseWhenCaretAtBeginning) {
					Close();
				} else {
					CompletionList.SelectItem(string.Empty);
				}
				return;
			}
			if (offset < this.StartOffset || offset > this.EndOffset) {
				if (CloseAutomatically) {
					Close();
				}
			} else {
				TextDocument document = this.TextArea.Document;
				if (document != null) {
					CompletionList.SelectItem(document.GetText(this.StartOffset, offset - this.StartOffset));
				}
			}
		}
	}
}
