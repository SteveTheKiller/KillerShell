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

using ICSharpCode.AvalonEdit.Document;

namespace ICSharpCode.AvalonEdit.Editing
{
	/// <summary>
	/// Represents a selected segment.
	/// </summary>
	public class SelectionSegment : ISegment
	{

		/// <summary>
		/// Creates a SelectionSegment from two offsets.
		/// </summary>
		public SelectionSegment(int startOffset, int endOffset)
		{
			this.StartOffset = Math.Min(startOffset, endOffset);
			this.EndOffset = Math.Max(startOffset, endOffset);
			this.StartVisualColumn = this.EndVisualColumn = -1;
		}

		/// <summary>
		/// Creates a SelectionSegment from two offsets and visual columns.
		/// </summary>
		public SelectionSegment(int startOffset, int startVC, int endOffset, int endVC)
		{
			if (startOffset < endOffset || (startOffset == endOffset && startVC <= endVC)) {
				this.StartOffset = startOffset;
				this.StartVisualColumn = startVC;
				this.EndOffset = endOffset;
				this.EndVisualColumn = endVC;
			} else {
				this.StartOffset = endOffset;
				this.StartVisualColumn = endVC;
				this.EndOffset = startOffset;
				this.EndVisualColumn = startVC;
			}
		}

		/// <summary>
		/// Gets the start offset.
		/// </summary>
		public int StartOffset { get; }

		/// <summary>
		/// Gets the end offset.
		/// </summary>
		public int EndOffset { get; }

		/// <summary>
		/// Gets the start visual column.
		/// </summary>
		public int StartVisualColumn { get; }

		/// <summary>
		/// Gets the end visual column.
		/// </summary>
		public int EndVisualColumn { get; }

		/// <inheritdoc/>
		int ISegment.Offset => StartOffset;

		/// <inheritdoc/>
		public int Length => EndOffset - StartOffset;

		/// <inheritdoc/>
		public override string ToString()
		{
			return string.Format("[SelectionSegment StartOffset={0}, EndOffset={1}, StartVC={2}, EndVC={3}]", StartOffset, EndOffset, StartVisualColumn, EndVisualColumn);
		}
	}
}
