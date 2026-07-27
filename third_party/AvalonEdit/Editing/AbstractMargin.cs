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
using System.Diagnostics;
using System.Windows;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ICSharpCode.AvalonEdit.Editing
{
	/// <summary>
	/// Base class for margins.
	/// Margins don't have to derive from this class, it just helps maintaining a reference to the TextView
	/// and the TextDocument.
	/// AbstractMargin derives from FrameworkElement, so if you don't want to handle visual children and rendering
	/// on your own, choose another base class for your margin!
	/// </summary>
	public abstract class AbstractMargin : FrameworkElement, ITextViewConnect
	{
		/// <summary>
		/// TextView property.
		/// </summary>
		public static readonly DependencyProperty TextViewProperty =
			DependencyProperty.Register("TextView", typeof(TextView), typeof(AbstractMargin),
										new FrameworkPropertyMetadata(OnTextViewChanged));

		/// <summary>
		/// Gets/sets the text view for which line numbers are displayed.
		/// </summary>
		/// <remarks>Adding a margin to <see cref="TextArea.LeftMargins"/> will automatically set this property to the text area's TextView.</remarks>
		// Nullable: a margin is detached by setting this to null, which DetachFromTextView does.
		public TextView? TextView {
			get => (TextView)GetValue(TextViewProperty); set => SetValue(TextViewProperty, value);
		}

		private static void OnTextViewChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
		{
			AbstractMargin margin = (AbstractMargin)dp;
			margin.wasAutoAddedToTextView = false;
			margin.OnTextViewChanged((TextView)e.OldValue, (TextView)e.NewValue);
		}

		// automatically set/unset TextView property using ITextViewConnect
		private bool wasAutoAddedToTextView;

		void ITextViewConnect.AddToTextView(TextView textView)
		{
			if (this.TextView == null) {
				this.TextView = textView;
				wasAutoAddedToTextView = true;
			} else if (this.TextView != textView) {
				throw new InvalidOperationException("This margin belongs to a different TextView.");
			}
		}

		void ITextViewConnect.RemoveFromTextView(TextView textView)
		{
			if (wasAutoAddedToTextView && this.TextView == textView) {
				this.TextView = null;
				Debug.Assert(!wasAutoAddedToTextView); // setting this.TextView should have unset this flag
			}
		}

		/// <summary>
		/// Gets the document associated with the margin.
		/// </summary>
		// A margin that is not attached to a TextView, or attached to one with no document, has
		// no document of its own. TextViewDocumentChanged sets this straight from TextView?.Document.
		public TextDocument? Document { get; private set; }

		/// <summary>
		/// Called when the <see cref="TextView"/> is changing.
		/// </summary>
		// Nullable both ways: the first attach passes null as the old view, and a margin is
		// detached by having its TextView set back to null.
		protected virtual void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
		{
			if (oldTextView != null) {
				oldTextView.DocumentChanged -= TextViewDocumentChanged;
			}
			if (newTextView != null) {
				newTextView.DocumentChanged += TextViewDocumentChanged;
			}
			// Was (null, null); the handler ignores both, and a null sender misreports the origin.
			TextViewDocumentChanged(this, EventArgs.Empty);
		}

		private void TextViewDocumentChanged(object sender, EventArgs e)
		{
			OnDocumentChanged(Document, TextView?.Document);
		}

		/// <summary>
		/// Called when the <see cref="Document"/> is changing.
		/// </summary>
		// Both nullable: a margin gains its first document from null and loses it back to null.
		protected virtual void OnDocumentChanged(TextDocument? oldDocument, TextDocument? newDocument)
		{
			Document = newDocument;
		}
	}
}
