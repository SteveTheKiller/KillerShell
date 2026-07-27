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
using System.Linq;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace ICSharpCode.AvalonEdit.Search
{
	internal class SearchResultBackgroundRenderer : IBackgroundRenderer
	{
		public TextSegmentCollection<SearchResult> CurrentResults { get; } = [];

		public KnownLayer Layer =>
				// draw behind selection
				KnownLayer.Selection;

		public SearchResultBackgroundRenderer()
		{
			MarkerBrush = Brushes.LightGreen;
			MarkerPen = null;
			MarkerCornerRadius = 3.0;
		}

		public Brush MarkerBrush { get; set; }

		// Nullable for real: the constructor sets it to null and nothing else assigns it unless
		// a caller wants an outline, so an unannotated Pen was a promise this class never kept.
		public Pen? MarkerPen { get; set; }
		public double MarkerCornerRadius { get; set; }

		public void Draw(TextView textView, DrawingContext drawingContext)
		{
			if (textView == null) {
				throw new ArgumentNullException("textView");
			}

			if (drawingContext == null) {
				throw new ArgumentNullException("drawingContext");
			}

			if (CurrentResults == null || !textView.VisualLinesValid) {
				return;
			}

			System.Collections.ObjectModel.ReadOnlyCollection<VisualLine> visualLines = textView.VisualLines;
			if (visualLines.Count == 0) {
				return;
			}

			int viewStart = visualLines.First().FirstDocumentLine.Offset;
			int viewEnd = visualLines.Last().LastDocumentLine.EndOffset;

			Brush markerBrush = MarkerBrush;
			Pen? markerPen = MarkerPen;
			double markerCornerRadius = MarkerCornerRadius;
			double markerPenThickness = markerPen != null ? markerPen.Thickness : 0;

			foreach (SearchResult result in CurrentResults.FindOverlappingSegments(viewStart, viewEnd - viewStart)) {
				BackgroundGeometryBuilder geoBuilder = new() {
					AlignToWholePixels = true,
					BorderThickness = markerPenThickness,
					CornerRadius = markerCornerRadius
				};
				geoBuilder.AddSegment(textView, result);
				Geometry? geometry = geoBuilder.CreateGeometry();
				if (geometry != null) {
					drawingContext.DrawGeometry(markerBrush, markerPen, geometry);
				}
			}
		}
	}
}
