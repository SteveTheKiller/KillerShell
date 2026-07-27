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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace ICSharpCode.AvalonEdit.Search
{
	/// <summary>
	/// Provides search functionality for AvalonEdit. It is displayed in the top-right corner of the TextArea.
	/// </summary>
	public class SearchPanel : Control
	{
		// Install is the only way to get one of these (the constructor is private) and it always
		// runs AttachInternal, so these four are never observed unset: null! rather than a null
		// check at every use.
		private TextArea textArea = null!;
		private SearchInputHandler handler = null!;
		private SearchResultBackgroundRenderer renderer = null!;
		private SearchPanelAdorner adorner = null!;

		// These three genuinely can be null and the code already tests them for it. The template
		// parts are whatever OnApplyTemplate found, which is nothing at all until a template has
		// been applied, and a TextArea with no document leaves currentDocument null.
		private TextDocument? currentDocument;
		private TextBox? searchTextBox;
		private Popup? dropdownPopup;

		#region DependencyProperties
		/// <summary>
		/// Dependency property for <see cref="UseRegex"/>.
		/// </summary>
		public static readonly DependencyProperty UseRegexProperty =
			DependencyProperty.Register("UseRegex", typeof(bool), typeof(SearchPanel),
										new FrameworkPropertyMetadata(false, SearchPatternChangedCallback));

		/// <summary>
		/// Gets/sets whether the search pattern should be interpreted as regular expression.
		/// </summary>
		public bool UseRegex {
			get => (bool)GetValue(UseRegexProperty); set => SetValue(UseRegexProperty, value);
		}

		/// <summary>
		/// Dependency property for <see cref="MatchCase"/>.
		/// </summary>
		public static readonly DependencyProperty MatchCaseProperty =
			DependencyProperty.Register("MatchCase", typeof(bool), typeof(SearchPanel),
										new FrameworkPropertyMetadata(false, SearchPatternChangedCallback));

		/// <summary>
		/// Gets/sets whether the search pattern should be interpreted case-sensitive.
		/// </summary>
		public bool MatchCase {
			get => (bool)GetValue(MatchCaseProperty); set => SetValue(MatchCaseProperty, value);
		}

		/// <summary>
		/// Dependency property for <see cref="WholeWords"/>.
		/// </summary>
		public static readonly DependencyProperty WholeWordsProperty =
			DependencyProperty.Register("WholeWords", typeof(bool), typeof(SearchPanel),
										new FrameworkPropertyMetadata(false, SearchPatternChangedCallback));

		/// <summary>
		/// Gets/sets whether the search pattern should only match whole words.
		/// </summary>
		public bool WholeWords {
			get => (bool)GetValue(WholeWordsProperty); set => SetValue(WholeWordsProperty, value);
		}

		/// <summary>
		/// Dependency property for <see cref="SearchPattern"/>.
		/// </summary>
		public static readonly DependencyProperty SearchPatternProperty =
			DependencyProperty.Register("SearchPattern", typeof(string), typeof(SearchPanel),
										new FrameworkPropertyMetadata("", SearchPatternChangedCallback));

		/// <summary>
		/// Gets/sets the search pattern.
		/// </summary>
		public string SearchPattern {
			get => (string)GetValue(SearchPatternProperty); set => SetValue(SearchPatternProperty, value);
		}

		/// <summary>
		/// Dependency property for <see cref="MarkerBrush"/>.
		/// </summary>
		public static readonly DependencyProperty MarkerBrushProperty =
			DependencyProperty.Register("MarkerBrush", typeof(Brush), typeof(SearchPanel),
										new FrameworkPropertyMetadata(Brushes.LightGreen, MarkerBrushChangedCallback));

		/// <summary>
		/// Gets/sets the Brush used for marking search results in the TextView.
		/// </summary>
		public Brush MarkerBrush {
			get => (Brush)GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value);
		}

		private static void MarkerBrushChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is SearchPanel panel) {
				panel.renderer.MarkerBrush = (Brush)e.NewValue;
			}
		}

		/// <summary>
		/// Dependency property for <see cref="MarkerPen"/>.
		/// </summary>
		public static readonly DependencyProperty MarkerPenProperty =
			DependencyProperty.Register("MarkerPen", typeof(Pen), typeof(SearchPanel),
										new PropertyMetadata(null, MarkerPenChangedCallback));

		/// <summary>
		/// Gets/sets the Pen used for marking search results in the TextView.
		/// </summary>
		public Pen MarkerPen {
			get => (Pen)GetValue(MarkerPenProperty); set => SetValue(MarkerPenProperty, value);
		}

		private static void MarkerPenChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is SearchPanel panel) {
				panel.renderer.MarkerPen = (Pen)e.NewValue;
			}
		}

		/// <summary>
		/// Dependency property for <see cref="MarkerCornerRadius"/>.
		/// </summary>
		public static readonly DependencyProperty MarkerCornerRadiusProperty =
			DependencyProperty.Register("MarkerCornerRadius", typeof(double), typeof(SearchPanel),
										new PropertyMetadata(3.0, MarkerCornerRadiusChangedCallback));

		/// <summary>
		/// Gets/sets the corner-radius used for marking search results in the TextView.
		/// </summary>
		public double MarkerCornerRadius {
			get => (double)GetValue(MarkerCornerRadiusProperty); set => SetValue(MarkerCornerRadiusProperty, value);
		}

		private static void MarkerCornerRadiusChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is SearchPanel panel) {
				panel.renderer.MarkerCornerRadius = (double)e.NewValue;
			}
		}

		/// <summary>
		/// Dependency property for <see cref="Localization"/>.
		/// </summary>
		public static readonly DependencyProperty LocalizationProperty =
			DependencyProperty.Register("Localization", typeof(Localization), typeof(SearchPanel),
										new FrameworkPropertyMetadata(new Localization()));

		/// <summary>
		/// Gets/sets the localization for the SearchPanel.
		/// </summary>
		public Localization Localization {
			get => (Localization)GetValue(LocalizationProperty); set => SetValue(LocalizationProperty, value);
		}
		#endregion

		static SearchPanel()
		{
			DefaultStyleKeyProperty.OverrideMetadata(typeof(SearchPanel), new FrameworkPropertyMetadata(typeof(SearchPanel)));
		}

		// Assigned by UpdateSearch, which the SearchPattern callback runs before DoSearch can
		// reach it: DoSearch only touches strategy once SearchPattern is non-empty, and setting
		// SearchPattern is what assigns this.
		private ISearchStrategy strategy = null!;

		private static void SearchPatternChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is SearchPanel panel) {
				panel.ValidateSearchText();
				panel.UpdateSearch();
			}
		}

		private void UpdateSearch()
		{
			// only reset as long as there are results
			// if no results are found, the "no matches found" message should not flicker.
			// if results are found by the next run, the message will be hidden inside DoSearch ...
			if (renderer.CurrentResults.Any()) {
				messageView.IsOpen = false;
			}

			strategy = SearchStrategyFactory.Create(SearchPattern ?? "", !MatchCase, WholeWords, UseRegex ? SearchMode.RegEx : SearchMode.Normal);
			// SearchPattern is a nullable dependency property and the line above already guards
			// it; the event args declare it non-null, so it was one unset box away from an NRE.
			OnSearchOptionsChanged(new SearchOptionsChangedEventArgs(SearchPattern ?? "", MatchCase, UseRegex, WholeWords));
			DoSearch(true);
		}

		/// <summary>
		/// Creates a new SearchPanel.
		/// </summary>
		private SearchPanel()
		{
		}

		/// <summary>
		/// Creates a SearchPanel and installs it to the TextEditor's TextArea.
		/// </summary>
		/// <remarks>This is a convenience wrapper.</remarks>
		public static SearchPanel Install(TextEditor editor)
		{
			if (editor == null) {
				throw new ArgumentNullException("editor");
			}

			return Install(editor.TextArea);
		}

		/// <summary>
		/// Creates a SearchPanel and installs it to the TextArea.
		/// </summary>
		public static SearchPanel Install(TextArea textArea)
		{
			if (textArea == null) {
				throw new ArgumentNullException("textArea");
			}

			SearchPanel panel = new();
			panel.AttachInternal(textArea);
			panel.handler = new SearchInputHandler(textArea, panel);
			textArea.DefaultInputHandler.NestedInputHandlers.Add(panel.handler);
			return panel;
		}

		/// <summary>
		/// Adds the commands used by SearchPanel to the given CommandBindingCollection.
		/// </summary>
		public void RegisterCommands(CommandBindingCollection commandBindings)
		{
			handler.RegisterGlobalCommands(commandBindings);
		}

		/// <summary>
		/// Removes the SearchPanel from the TextArea.
		/// </summary>
		public void Uninstall()
		{
			Close();
			textArea.DocumentChanged -= textArea_DocumentChanged;
			if (currentDocument != null) {
				currentDocument.TextChanged -= textArea_Document_TextChanged;
			}

			textArea.DefaultInputHandler.NestedInputHandlers.Remove(handler);
		}

		private void AttachInternal(TextArea textArea)
		{
			this.textArea = textArea;
			adorner = new SearchPanelAdorner(textArea, this);
			DataContext = this;

			renderer = new SearchResultBackgroundRenderer();
			currentDocument = textArea.Document;
			if (currentDocument != null) {
				currentDocument.TextChanged += textArea_Document_TextChanged;
			}

			textArea.DocumentChanged += textArea_DocumentChanged;
			KeyDown += SearchLayerKeyDown;

			CommandBindings.Add(new CommandBinding(SearchCommands.FindNext, (sender, e) => FindNext()));
			CommandBindings.Add(new CommandBinding(SearchCommands.FindPrevious, (sender, e) => FindPrevious()));
			CommandBindings.Add(new CommandBinding(SearchCommands.CloseSearchPanel, (sender, e) => Close()));
			IsClosed = true;
		}

		private void textArea_DocumentChanged(object sender, EventArgs e)
		{
			if (currentDocument != null) {
				currentDocument.TextChanged -= textArea_Document_TextChanged;
			}

			currentDocument = textArea.Document;
			if (currentDocument != null) {
				currentDocument.TextChanged += textArea_Document_TextChanged;
				DoSearch(false);
			}
		}

		private void textArea_Document_TextChanged(object sender, EventArgs e)
		{
			DoSearch(false);
		}

		/// <inheritdoc/>
		public override void OnApplyTemplate()
		{
			base.OnApplyTemplate();

			searchTextBox = Template.FindName("PART_searchTextBox", this) as TextBox;
			dropdownPopup = Template.FindName("PART_dropdownPopup", this) as Popup;
		}

		private void ValidateSearchText()
		{
			if (searchTextBox == null) {
				return;
			}

			System.Windows.Data.BindingExpression be = searchTextBox.GetBindingExpression(TextBox.TextProperty);

			try {
				if (be != null) {
					Validation.ClearInvalid(be);
				}

				UpdateSearch();

			} catch (SearchPatternException ex) {
				ValidationError ve = new(be.ParentBinding.ValidationRules[0], be, ex.Message, ex);
				Validation.MarkInvalid(be, ve);
			}
		}

		/// <summary>
		/// Reactivates the SearchPanel by setting the focus on the search box and selecting all text.
		/// </summary>
		public void Reactivate()
		{
			if (searchTextBox == null) {
				return;
			}

			searchTextBox.Focus();
			searchTextBox.SelectAll();
		}

		/// <summary>
		/// Moves to the next occurrence in the file.
		/// </summary>
		public void FindNext()
		{
			SearchResult? result = renderer.CurrentResults.FindFirstSegmentWithStartAfter(textArea.Caret.Offset + 1) ?? renderer.CurrentResults.FirstSegment;
			if (result != null) {
				SelectResult(result);
			}
		}

		/// <summary>
		/// Moves to the previous occurrence in the file.
		/// </summary>
		public void FindPrevious()
		{
			SearchResult? result = renderer.CurrentResults.FindFirstSegmentWithStartAfter(textArea.Caret.Offset);
			if (result != null) {
				result = renderer.CurrentResults.GetPreviousSegment(result);
			}

			result ??= renderer.CurrentResults.LastSegment;

			if (result != null) {
				SelectResult(result);
			}
		}

		private readonly ToolTip messageView = new() { Placement = PlacementMode.Bottom, StaysOpen = true, Focusable = false };

		private void DoSearch(bool changeSelection)
		{
			if (IsClosed) {
				return;
			}

			renderer.CurrentResults.Clear();

			if (!string.IsNullOrEmpty(SearchPattern)) {
				int offset = textArea.Caret.Offset;
				if (changeSelection) {
					textArea.ClearSelection();
				}
				// We cast from ISearchResult to SearchResult; this is safe because we always use the built-in strategy
				foreach (SearchResult result in strategy.FindAll(textArea.Document, 0, textArea.Document.TextLength).Cast<SearchResult>()) {
					if (changeSelection && result.StartOffset >= offset) {
						SelectResult(result);
						changeSelection = false;
					}
					renderer.CurrentResults.Add(result);
				}
				if (!renderer.CurrentResults.Any()) {
					messageView.IsOpen = true;
					messageView.Content = Localization.NoMatchesFoundText;
					messageView.PlacementTarget = searchTextBox;
				} else {
					messageView.IsOpen = false;
				}
			}
			textArea.TextView.InvalidateLayer(KnownLayer.Selection);
		}

		private void SelectResult(SearchResult result)
		{
			textArea.Caret.Offset = result.StartOffset;
			textArea.Selection = Selection.Create(textArea, result.StartOffset, result.EndOffset);
			textArea.Caret.BringCaretToView();
			// show caret even if the editor does not have the Keyboard Focus
			textArea.Caret.Show();
		}

		private void SearchLayerKeyDown(object sender, KeyEventArgs e)
		{
			switch (e.Key) {
				case Key.Enter:
					e.Handled = true;
					if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) {
						FindPrevious();
					} else {
						FindNext();
					}

					if (searchTextBox != null) {
						ValidationError error = Validation.GetErrors(searchTextBox).FirstOrDefault();
						if (error != null) {
							messageView.Content = Localization.ErrorText + " " + error.ErrorContent;
							messageView.PlacementTarget = searchTextBox;
							messageView.IsOpen = true;
						}
					}
					break;
				case Key.Escape:
					e.Handled = true;
					Close();
					break;
			}
		}

		/// <summary>
		/// Gets whether the Panel is already closed.
		/// </summary>
		public bool IsClosed { get; private set; }

		/// <summary>
		/// Closes the SearchPanel.
		/// </summary>
		public void Close()
		{
			bool hasFocus = IsKeyboardFocusWithin;

			AdornerLayer layer = AdornerLayer.GetAdornerLayer(textArea);
			layer?.Remove(adorner);
			if (dropdownPopup != null) {
				dropdownPopup.IsOpen = false;
			}

			messageView.IsOpen = false;
			textArea.TextView.BackgroundRenderers.Remove(renderer);
			if (hasFocus) {
				textArea.Focus();
			}

			IsClosed = true;

			// Clear existing search results so that the segments don't have to be maintained
			renderer.CurrentResults.Clear();
		}

		/// <summary>
		/// Opens the an existing search panel.
		/// </summary>
		public void Open()
		{
			if (!IsClosed) {
				return;
			}

			AdornerLayer layer = AdornerLayer.GetAdornerLayer(textArea);
			layer?.Add(adorner);
			textArea.TextView.BackgroundRenderers.Add(renderer);
			IsClosed = false;
			DoSearch(false);
		}

		/// <summary>
		/// Fired when SearchOptions are changed inside the SearchPanel.
		/// </summary>
		public event EventHandler<SearchOptionsChangedEventArgs>? SearchOptionsChanged;

		/// <summary>
		/// Raises the <see cref="SearchOptionsChanged" /> event.
		/// </summary>
		protected virtual void OnSearchOptionsChanged(SearchOptionsChangedEventArgs e)
		{
			SearchOptionsChanged?.Invoke(this, e);
		}
	}

	/// <summary>
	/// EventArgs for <see cref="SearchPanel.SearchOptionsChanged"/> event.
	/// </summary>
	public class SearchOptionsChangedEventArgs : EventArgs
	{
		/// <summary>
		/// Gets the search pattern.
		/// </summary>
		public string SearchPattern { get; private set; }

		/// <summary>
		/// Gets whether the search pattern should be interpreted case-sensitive.
		/// </summary>
		public bool MatchCase { get; private set; }

		/// <summary>
		/// Gets whether the search pattern should be interpreted as regular expression.
		/// </summary>
		public bool UseRegex { get; private set; }

		/// <summary>
		/// Gets whether the search pattern should only match whole words.
		/// </summary>
		public bool WholeWords { get; private set; }

		/// <summary>
		/// Creates a new SearchOptionsChangedEventArgs instance.
		/// </summary>
		public SearchOptionsChangedEventArgs(string searchPattern, bool matchCase, bool useRegex, bool wholeWords)
		{
			SearchPattern = searchPattern;
			MatchCase = matchCase;
			UseRegex = useRegex;
			WholeWords = wholeWords;
		}
	}

	internal class SearchPanelAdorner : Adorner
	{
		private readonly SearchPanel panel;

		public SearchPanelAdorner(TextArea textArea, SearchPanel panel)
			: base(textArea)
		{
			this.panel = panel;
			AddVisualChild(panel);
		}

		protected override int VisualChildrenCount => 1;

		protected override Visual GetVisualChild(int index)
		{
			if (index != 0) {
				throw new ArgumentOutOfRangeException();
			}

			return panel;
		}

		protected override Size ArrangeOverride(Size finalSize)
		{
			panel.Arrange(new Rect(new Point(0, 0), finalSize));
			return new Size(panel.ActualWidth, panel.ActualHeight);
		}
	}
}
