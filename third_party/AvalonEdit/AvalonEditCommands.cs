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

using System.Windows.Input;

namespace ICSharpCode.AvalonEdit
{
	/// <summary>
	/// Custom commands for AvalonEdit.
	/// </summary>
	public static class AvalonEditCommands
	{
		/// <summary>
		/// Toggles Overstrike mode
		/// The default shortcut is Ins.
		/// </summary>
		public static readonly RoutedCommand ToggleOverstrike = new(
			"ToggleOverstrike", typeof(TextEditor),
			[
				new KeyGesture(Key.Insert)
			]);

		/// <summary>
		/// Deletes the current line.
		/// The default shortcut is Ctrl+D.
		/// </summary>
		public static readonly RoutedCommand DeleteLine = new(
			"DeleteLine", typeof(TextEditor),
			[
				new KeyGesture(Key.D, ModifierKeys.Control)
			]);

		/// <summary>
		/// Removes leading whitespace from the selected lines (or the whole document if the selection is empty).
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "Whitespace",
														 Justification = "WPF uses 'Whitespace'")]
		public static readonly RoutedCommand RemoveLeadingWhitespace = new("RemoveLeadingWhitespace", typeof(TextEditor));

		/// <summary>
		/// Removes trailing whitespace from the selected lines (or the whole document if the selection is empty).
		/// </summary>
		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1702:CompoundWordsShouldBeCasedCorrectly", MessageId = "Whitespace",
														 Justification = "WPF uses 'Whitespace'")]
		public static readonly RoutedCommand RemoveTrailingWhitespace = new("RemoveTrailingWhitespace", typeof(TextEditor));

		/// <summary>
		/// Converts the selected text to upper case.
		/// </summary>
		public static readonly RoutedCommand ConvertToUppercase = new("ConvertToUppercase", typeof(TextEditor));

		/// <summary>
		/// Converts the selected text to lower case.
		/// </summary>
		public static readonly RoutedCommand ConvertToLowercase = new("ConvertToLowercase", typeof(TextEditor));

		/// <summary>
		/// Converts the selected text to title case.
		/// </summary>
		public static readonly RoutedCommand ConvertToTitleCase = new("ConvertToTitleCase", typeof(TextEditor));

		/// <summary>
		/// Inverts the case of the selected text.
		/// </summary>
		public static readonly RoutedCommand InvertCase = new("InvertCase", typeof(TextEditor));

		/// <summary>
		/// Converts tabs to spaces in the selected text.
		/// </summary>
		public static readonly RoutedCommand ConvertTabsToSpaces = new("ConvertTabsToSpaces", typeof(TextEditor));

		/// <summary>
		/// Converts spaces to tabs in the selected text.
		/// </summary>
		public static readonly RoutedCommand ConvertSpacesToTabs = new("ConvertSpacesToTabs", typeof(TextEditor));

		/// <summary>
		/// Converts leading tabs to spaces in the selected lines (or the whole document if the selection is empty).
		/// </summary>
		public static readonly RoutedCommand ConvertLeadingTabsToSpaces = new("ConvertLeadingTabsToSpaces", typeof(TextEditor));

		/// <summary>
		/// Converts leading spaces to tabs in the selected lines (or the whole document if the selection is empty).
		/// </summary>
		public static readonly RoutedCommand ConvertLeadingSpacesToTabs = new("ConvertLeadingSpacesToTabs", typeof(TextEditor));

		/// <summary>
		/// Runs the IIndentationStrategy on the selected lines (or the whole document if the selection is empty).
		/// </summary>
		public static readonly RoutedCommand IndentSelection = new(
			"IndentSelection", typeof(TextEditor),
			[
				new KeyGesture(Key.I, ModifierKeys.Control)
			]);
	}
}
