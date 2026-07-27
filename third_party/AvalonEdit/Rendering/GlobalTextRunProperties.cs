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

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace ICSharpCode.AvalonEdit.Rendering
{
	internal sealed class GlobalTextRunProperties : TextRunProperties
	{
		// Set by TextView immediately after construction, never left null in practice, so null!
		// rather than a constructor: this type is filled field by field on purpose.
		internal Typeface typeface = null!;
		internal double fontRenderingEmSize;
		internal Brush foregroundBrush = null!;
		internal System.Globalization.CultureInfo cultureInfo = null!;

		public override Typeface Typeface => typeface;
		public override double FontRenderingEmSize => fontRenderingEmSize;
		public override double FontHintingEmSize => fontRenderingEmSize;
		public override TextDecorationCollection? TextDecorations => null;
		public override Brush ForegroundBrush => foregroundBrush;

		// KillerShell: upstream backs this with an internal backgroundBrush field that nothing in
		// the assembly ever assigns (CS0649), so the property could only ever return null. The
		// dead field is gone and the null is now stated outright, the same shape TextDecorations
		// and TextEffects already use. A background comes from the renderer layers, not from here.
		public override Brush? BackgroundBrush => null;
		public override System.Globalization.CultureInfo CultureInfo => cultureInfo;
		public override TextEffectCollection? TextEffects => null;
	}
}
