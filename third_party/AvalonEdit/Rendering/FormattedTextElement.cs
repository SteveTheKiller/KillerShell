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
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Rendering
{
	/// <summary>
	/// Formatted text (not normal document text).
	/// This is used as base class for various VisualLineElements that are displayed using a
	/// FormattedText, for example newline markers or collapsed folding sections.
	/// </summary>
	public class FormattedTextElement : VisualLineElement
	{
		// Exactly one of the three constructors below fills one of these in; the other two stay
		// null. text is additionally dropped once CreateTextRun has turned it into a textLine.
		internal readonly FormattedText? formattedText;
		internal string? text;
		internal TextLine? textLine;

		/// <summary>
		/// Creates a new FormattedTextElement that displays the specified text
		/// and occupies the specified length in the document.
		/// </summary>
		public FormattedTextElement(string text, int documentLength) : base(1, documentLength)
		{
			this.text = text ?? throw new ArgumentNullException("text");
			this.BreakBefore = LineBreakCondition.BreakPossible;
			this.BreakAfter = LineBreakCondition.BreakPossible;
		}

		/// <summary>
		/// Creates a new FormattedTextElement that displays the specified text
		/// and occupies the specified length in the document.
		/// </summary>
		public FormattedTextElement(TextLine text, int documentLength) : base(1, documentLength)
		{
			this.textLine = text ?? throw new ArgumentNullException("text");
			this.BreakBefore = LineBreakCondition.BreakPossible;
			this.BreakAfter = LineBreakCondition.BreakPossible;
		}

		/// <summary>
		/// Creates a new FormattedTextElement that displays the specified text
		/// and occupies the specified length in the document.
		/// </summary>
		public FormattedTextElement(FormattedText text, int documentLength) : base(1, documentLength)
		{
			this.formattedText = text ?? throw new ArgumentNullException("text");
			this.BreakBefore = LineBreakCondition.BreakPossible;
			this.BreakAfter = LineBreakCondition.BreakPossible;
		}

		/// <summary>
		/// Gets/sets the line break condition before the element.
		/// The default is 'BreakPossible'.
		/// </summary>
		public LineBreakCondition BreakBefore { get; set; }

		/// <summary>
		/// Gets/sets the line break condition after the element.
		/// The default is 'BreakPossible'.
		/// </summary>
		public LineBreakCondition BreakAfter { get; set; }

		/// <inheritdoc/>
		public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
		{
			if (textLine == null) {
				TextFormatter formatter = TextFormatterFactory.Create(context.TextView);
				// No textLine means this element was built from a string, so text is set.
				textLine = PrepareText(formatter, this.text!, this.TextRunProperties);
				this.text = null;
			}
			return new FormattedTextRun(this, this.TextRunProperties);
		}

		/// <summary>
		/// Constructs a TextLine from a simple text.
		/// </summary>
		public static TextLine PrepareText(TextFormatter formatter, string text, TextRunProperties properties)
		{
			if (formatter == null) {
				throw new ArgumentNullException("formatter");
			}

			if (text == null) {
				throw new ArgumentNullException("text");
			}

			if (properties == null) {
				throw new ArgumentNullException("properties");
			}

			return formatter.FormatLine(
				new SimpleTextSource(text, properties),
				0,
				32000,
				new VisualLineTextParagraphProperties {
					defaultTextRunProperties = properties,
					textWrapping = TextWrapping.NoWrap,
					tabSize = 40
				},
				null);
		}
	}

	/// <summary>
	/// This is the TextRun implementation used by the <see cref="FormattedTextElement"/> class.
	/// </summary>
	public class FormattedTextRun : TextEmbeddedObject
	{
		private readonly TextRunProperties properties;

		/// <summary>
		/// Creates a new FormattedTextRun.
		/// </summary>
		public FormattedTextRun(FormattedTextElement element, TextRunProperties properties)
		{
			this.properties = properties ?? throw new ArgumentNullException("properties");
			this.Element = element ?? throw new ArgumentNullException("element");
		}

		/// <summary>
		/// Gets the element for which the FormattedTextRun was created.
		/// </summary>
		public FormattedTextElement Element { get; }

		/// <inheritdoc/>
		public override LineBreakCondition BreakBefore => Element.BreakBefore;

		/// <inheritdoc/>
		public override LineBreakCondition BreakAfter => Element.BreakAfter;

		/// <inheritdoc/>
		public override bool HasFixedSize => true;

		/// <inheritdoc/>
		public override CharacterBufferReference CharacterBufferReference => new();

		/// <inheritdoc/>
		public override int Length => Element.VisualLength;

		/// <inheritdoc/>
		public override TextRunProperties Properties => properties;

		/// <inheritdoc/>
		public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth)
		{
			// No FormattedText means the element carries a TextLine instead - CreateTextRun has
			// already run by the time the formatter asks for metrics, so one of the two is set.
			FormattedText? formattedText = Element.formattedText;
			if (formattedText != null) {
				return new TextEmbeddedObjectMetrics(formattedText.WidthIncludingTrailingWhitespace,
													 formattedText.Height,
													 formattedText.Baseline);
			} else {
				TextLine text = Element.textLine!;
				return new TextEmbeddedObjectMetrics(text.WidthIncludingTrailingWhitespace,
													 text.Height,
													 text.Baseline);
			}
		}

		/// <inheritdoc/>
		public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways)
		{
			// Same either-or as in Format above.
			FormattedText? formattedText = Element.formattedText;
			if (formattedText != null) {
				return new Rect(0, 0, formattedText.WidthIncludingTrailingWhitespace, formattedText.Height);
			} else {
				TextLine text = Element.textLine!;
				return new Rect(0, 0, text.WidthIncludingTrailingWhitespace, text.Height);
			}
		}

		/// <inheritdoc/>
		public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways)
		{
			if (Element.formattedText != null) {
				origin.Y -= Element.formattedText.Baseline;
				drawingContext.DrawText(Element.formattedText, origin);
			} else {
				// Same either-or again.
				origin.Y -= Element.textLine!.Baseline;
				Element.textLine.Draw(drawingContext, origin, InvertAxes.None);
			}
		}
	}
}
