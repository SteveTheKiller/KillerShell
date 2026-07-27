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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;

using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Highlighting.Xshd
{
	[Serializable]
	internal sealed class XmlHighlightingDefinition : IHighlightingDefinition
	{
		// Carries the .xshd's own name, which may be absent - see XshdSyntaxDefinition.Name.
		public string? Name { get; private set; }

		public XmlHighlightingDefinition(XshdSyntaxDefinition xshd, IHighlightingDefinitionReferenceResolver resolver)
		{
			this.Name = xshd.Name;
			// Create HighlightingRuleSet instances
			RegisterNamedElementsVisitor rnev = new(this);
			xshd.AcceptElements(rnev);
			// Assign MainRuleSet so that references can be resolved
			foreach (XshdElement element in xshd.Elements) {
				if (element is XshdRuleSet xrs && xrs.Name == null) {
					if (MainRuleSet != null) {
						throw Error(element, "Duplicate main RuleSet. There must be only one nameless RuleSet!");
					} else {
						MainRuleSet = rnev.ruleSets[xrs];
					}
				}
			}
			if (MainRuleSet == null) {
				throw new HighlightingDefinitionInvalidException("Could not find main RuleSet.");
			}
			// Translate elements within the rulesets (resolving references and processing imports)
			xshd.AcceptElements(new TranslateElementVisitor(this, rnev.ruleSets, resolver));

			foreach (XshdProperty p in xshd.Elements.OfType<XshdProperty>()) {
				// The schema requires both attributes on a <Property>, so neither is null here.
				propDict.Add(p.Name!, p.Value!);
			}
		}

		#region RegisterNamedElements
		private sealed class RegisterNamedElementsVisitor : IXshdVisitor
		{
			private readonly XmlHighlightingDefinition def;
			internal readonly Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSets
				= [];

			public RegisterNamedElementsVisitor(XmlHighlightingDefinition def)
			{
				this.def = def;
			}

			// This visitor only registers names; nothing it visits produces a value, so every
			// method returns null (or forwards a reference's null).
			public object? VisitRuleSet(XshdRuleSet ruleSet)
			{
				HighlightingRuleSet hrs = new();
				ruleSets.Add(ruleSet, hrs);
				if (ruleSet.Name != null) {
					if (ruleSet.Name.Length == 0) {
						throw Error(ruleSet, "Name must not be the empty string");
					}

					if (def.ruleSetDict.ContainsKey(ruleSet.Name)) {
						throw Error(ruleSet, "Duplicate rule set name '" + ruleSet.Name + "'.");
					}

					def.ruleSetDict.Add(ruleSet.Name, hrs);
				}
				ruleSet.AcceptElements(this);
				return null;
			}

			public object? VisitColor(XshdColor color)
			{
				if (color.Name != null) {
					if (color.Name.Length == 0) {
						throw Error(color, "Name must not be the empty string");
					}

					if (def.colorDict.ContainsKey(color.Name)) {
						throw Error(color, "Duplicate color name '" + color.Name + "'.");
					}

					def.colorDict.Add(color.Name, new HighlightingColor());
				}
				return null;
			}

			public object? VisitKeywords(XshdKeywords keywords)
			{
				return keywords.ColorReference.AcceptVisitor(this);
			}

			public object? VisitSpan(XshdSpan span)
			{
				span.BeginColorReference.AcceptVisitor(this);
				span.SpanColorReference.AcceptVisitor(this);
				span.EndColorReference.AcceptVisitor(this);
				return span.RuleSetReference.AcceptVisitor(this);
			}

			public object? VisitImport(XshdImport import)
			{
				return import.RuleSetReference.AcceptVisitor(this);
			}

			public object? VisitRule(XshdRule rule)
			{
				return rule.ColorReference.AcceptVisitor(this);
			}
		}
		#endregion

		#region TranslateElements
		private sealed class TranslateElementVisitor : IXshdVisitor
		{
			private readonly XmlHighlightingDefinition def;
			private readonly Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSetDict;
			private readonly Dictionary<HighlightingRuleSet, XshdRuleSet> reverseRuleSetDict;
			private readonly IHighlightingDefinitionReferenceResolver resolver;
			private readonly HashSet<XshdRuleSet> processingStartedRuleSets = [];
			private readonly HashSet<XshdRuleSet> processedRuleSets = [];
			private bool ignoreCase;

			// The two null asserts that used to be here are gone: the parameter types state them,
			// and asserting made every later use of def and ruleSetDict read as possibly-null.
			public TranslateElementVisitor(XmlHighlightingDefinition def, Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSetDict, IHighlightingDefinitionReferenceResolver resolver)
			{
				this.def = def;
				this.ruleSetDict = ruleSetDict;
				this.resolver = resolver;
				reverseRuleSetDict = [];
				foreach (KeyValuePair<XshdRuleSet, HighlightingRuleSet> pair in ruleSetDict) {
					reverseRuleSetDict.Add(pair.Value, pair.Key);
				}
			}

			// Returns the translated element, or null where an element produces nothing (an empty
			// color, for instance), which is why the interface return type is nullable.
			public object? VisitRuleSet(XshdRuleSet ruleSet)
			{
				HighlightingRuleSet rs = ruleSetDict[ruleSet];
				if (processedRuleSets.Contains(ruleSet)) {
					return rs;
				}

				if (!processingStartedRuleSets.Add(ruleSet)) {
					throw Error(ruleSet, "RuleSet cannot be processed because it contains cyclic <Import>");
				}

				bool oldIgnoreCase = ignoreCase;
				if (ruleSet.IgnoreCase != null) {
					ignoreCase = ruleSet.IgnoreCase.Value;
				}

				rs.Name = ruleSet.Name;

				foreach (XshdElement element in ruleSet.Elements) {
					object? o = element.AcceptVisitor(this);
					if (o is HighlightingRuleSet elementRuleSet) {
						Merge(rs, elementRuleSet);
					} else {
						if (o is HighlightingSpan span) {
							rs.Spans.Add(span);
						} else {
							if (o is HighlightingRule elementRule) {
								rs.Rules.Add(elementRule);
							}
						}
					}
				}

				ignoreCase = oldIgnoreCase;
				processedRuleSets.Add(ruleSet);

				return rs;
			}

			private static void Merge(HighlightingRuleSet target, HighlightingRuleSet source)
			{
				target.Rules.AddRange(source.Rules);
				target.Spans.AddRange(source.Spans);
			}

			public object? VisitColor(XshdColor color)
			{
				HighlightingColor c;
				if (color.Name != null) {
					c = def.colorDict[color.Name];
				} else if (color.Foreground == null && color.Background == null && color.Underline == null && color.FontStyle == null && color.FontWeight == null) {
					return null;
				} else {
					c = new HighlightingColor();
				}

				c.Name = color.Name;
				c.Foreground = color.Foreground;
				c.Background = color.Background;
				c.Underline = color.Underline;
				c.Strikethrough = color.Strikethrough;
				c.FontStyle = color.FontStyle;
				c.FontWeight = color.FontWeight;
				c.FontFamily = color.FontFamily;
				c.FontSize = color.FontSize;
				return c;
			}

			public object? VisitKeywords(XshdKeywords keywords)
			{
				if (keywords.Words.Count == 0) {
					return Error(keywords, "Keyword group must not be empty.");
				}

				foreach (string keyword in keywords.Words) {
					if (string.IsNullOrEmpty(keyword)) {
						throw Error(keywords, "Cannot use empty string as keyword");
					}
				}
				StringBuilder keyWordRegex = new();
				// We can use "\b" only where the keyword starts/ends with a letter or digit, otherwise we don't
				// highlight correctly. (example: ILAsm-Mode.xshd with ".maxstack" keyword)
				if (keywords.Words.All(IsSimpleWord)) {
					keyWordRegex.Append(@"\b(?>");
					// (?> = atomic group
					// atomic groups increase matching performance, but we
					// must ensure that the keywords are sorted correctly.
					// "\b(?>in|int)\b" does not match "int" because the atomic group captures "in".
					// To solve this, we are sorting the keywords by descending length.
					int i = 0;
					foreach (string keyword in keywords.Words.OrderByDescending(w => w.Length)) {
						if (i++ > 0) {
							keyWordRegex.Append('|');
						}

						keyWordRegex.Append(Regex.Escape(keyword));
					}
					keyWordRegex.Append(@")\b");
				} else {
					keyWordRegex.Append("(?>");
					int i = 0;
					foreach (string keyword in keywords.Words.OrderByDescending(w => w.Length)) {
						if (i++ > 0) {
							keyWordRegex.Append('|');
						}

						if (char.IsLetterOrDigit(keyword[0])) {
							keyWordRegex.Append(@"\b");
						}

						keyWordRegex.Append(Regex.Escape(keyword));
						if (char.IsLetterOrDigit(keyword[^1])) {
							keyWordRegex.Append(@"\b");
						}
					}
					keyWordRegex.Append(')');
				}
				return new HighlightingRule {
					Color = GetColor(keywords, keywords.ColorReference),
					Regex = CreateRegex(keywords, keyWordRegex.ToString(), XshdRegexType.Default)
				};
			}

			private static bool IsSimpleWord(string word)
			{
				return char.IsLetterOrDigit(word[0]) && char.IsLetterOrDigit(word, word.Length - 1);
			}

			// Nullable in, never null out: a missing regex is a definition error, reported with the
			// element's line number rather than left to fail later.
			private Regex CreateRegex(XshdElement position, string? regex, XshdRegexType regexType)
			{
				if (regex == null) {
					throw Error(position, "Regex missing");
				}

				RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
				if (regexType == XshdRegexType.IgnorePatternWhitespace) {
					options |= RegexOptions.IgnorePatternWhitespace;
				}

				if (ignoreCase) {
					options |= RegexOptions.IgnoreCase;
				}

				try {
					return new Regex(regex, options);
				} catch (ArgumentException ex) {
					throw Error(position, ex.Message);
				}
			}

			// Null when the reference is empty, which is the ordinary case for a rule that sets no
			// color of its own.
			private HighlightingColor? GetColor(XshdElement position, XshdReference<XshdColor> colorReference)
			{
				if (colorReference.InlineElement != null) {
					return (HighlightingColor?)colorReference.InlineElement.AcceptVisitor(this);
				} else if (colorReference.ReferencedElement != null) {
					IHighlightingDefinition definition = GetDefinition(position, colorReference.ReferencedDefinition);
					HighlightingColor color = definition.GetNamedColor(colorReference.ReferencedElement) ?? throw Error(position, "Could not find color named '" + colorReference.ReferencedElement + "'.");
					return color;
				} else {
					return null;
				}
			}

			// A null definitionName means "this definition", which is how an unqualified reference
			// is written in the .xshd file.
			private IHighlightingDefinition GetDefinition(XshdElement position, string? definitionName)
			{
				if (definitionName == null) {
					return def;
				}

				if (resolver == null) {
					throw Error(position, "Resolving references to other syntax definitions is not possible because the IHighlightingDefinitionReferenceResolver is null.");
				}

				IHighlightingDefinition d = resolver.GetDefinition(definitionName) ?? throw Error(position, "Could not find definition with name '" + definitionName + "'.");
				return d;
			}

			// Null when the reference is empty - a span with no ruleSet attribute highlights
			// nothing inside itself, which is a legitimate thing to write.
			private HighlightingRuleSet? GetRuleSet(XshdElement position, XshdReference<XshdRuleSet> ruleSetReference)
			{
				if (ruleSetReference.InlineElement != null) {
					return (HighlightingRuleSet?)ruleSetReference.InlineElement.AcceptVisitor(this);
				} else if (ruleSetReference.ReferencedElement != null) {
					IHighlightingDefinition definition = GetDefinition(position, ruleSetReference.ReferencedDefinition);
					HighlightingRuleSet ruleSet = definition.GetNamedRuleSet(ruleSetReference.ReferencedElement) ?? throw Error(position, "Could not find rule set named '" + ruleSetReference.ReferencedElement + "'.");
					return ruleSet;
				} else {
					return null;
				}
			}

			public object? VisitSpan(XshdSpan span)
			{
				string? endRegex = span.EndRegex;
				if (string.IsNullOrEmpty(span.BeginRegex) && string.IsNullOrEmpty(span.EndRegex)) {
					throw Error(span, "Span has no start/end regex.");
				}

				if (!span.Multiline) {
					if (endRegex == null) {
						endRegex = "$";
					} else if (span.EndRegexType == XshdRegexType.IgnorePatternWhitespace) {
						endRegex = "($|" + endRegex + "\n)";
					} else {
						endRegex = "($|" + endRegex + ")";
					}
				}
				HighlightingColor? wholeSpanColor = GetColor(span, span.SpanColorReference);
				return new HighlightingSpan {
					StartExpression = CreateRegex(span, span.BeginRegex, span.BeginRegexType),
					EndExpression = CreateRegex(span, endRegex, span.EndRegexType),
					RuleSet = GetRuleSet(span, span.RuleSetReference),
					StartColor = GetColor(span, span.BeginColorReference),
					SpanColor = wholeSpanColor,
					EndColor = GetColor(span, span.EndColorReference),
					SpanColorIncludesStart = true,
					SpanColorIncludesEnd = true
				};
			}

			public object? VisitImport(XshdImport import)
			{
				HighlightingRuleSet? hrs = GetRuleSet(import, import.RuleSetReference);
				// An <Import> with no rule set imports nothing, so there is nothing to look up.
				if (hrs != null && reverseRuleSetDict.TryGetValue(hrs, out XshdRuleSet inputRuleSet)) {
					// ensure the ruleset is processed before importing its members
					if (VisitRuleSet(inputRuleSet) != hrs) {
						Debug.Fail("this shouldn't happen");
					}
				}
				return hrs;
			}

			public object? VisitRule(XshdRule rule)
			{
				return new HighlightingRule {
					Color = GetColor(rule, rule.ColorReference),
					Regex = CreateRegex(rule, rule.Regex, rule.RegexType)
				};
			}
		}
		#endregion

		private static Exception Error(XshdElement element, string message)
		{
			if (element.LineNumber > 0) {
				return new HighlightingDefinitionInvalidException(
					"Error at line " + element.LineNumber + ":\n" + message);
			} else {
				return new HighlightingDefinitionInvalidException(message);
			}
		}

		private readonly Dictionary<string, HighlightingRuleSet> ruleSetDict = [];
		private readonly Dictionary<string, HighlightingColor> colorDict = [];
		[OptionalField]
		private readonly Dictionary<string, string> propDict = [];

		// Set in the constructor, which throws if the .xshd has no nameless rule set, so by the
		// time anyone can reach this it is there.
		public HighlightingRuleSet MainRuleSet { get; private set; } = null!;

		public HighlightingRuleSet? GetNamedRuleSet(string name)
		{
			if (string.IsNullOrEmpty(name)) {
				return MainRuleSet;
			}

			if (ruleSetDict.TryGetValue(name, out HighlightingRuleSet r)) {
				return r;
			} else {
				return null;
			}
		}

		public HighlightingColor? GetNamedColor(string name)
		{
			if (colorDict.TryGetValue(name, out HighlightingColor c)) {
				return c;
			} else {
				return null;
			}
		}

		public IEnumerable<HighlightingColor> NamedHighlightingColors => colorDict.Values;

		public override string ToString()
		{
			return this.Name ?? string.Empty;
		}

		public IDictionary<string, string> Properties => propDict;
	}
}
