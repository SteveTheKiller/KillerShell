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
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Xml;

using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Highlighting
{
	/// <summary>
	/// Manages a list of syntax highlighting definitions.
	/// </summary>
	/// <remarks>
	/// All members on this class (including instance members) are thread-safe.
	/// </remarks>
	public class HighlightingManager : IHighlightingDefinitionReferenceResolver
	{
		private sealed class DelayLoadedHighlightingDefinition : IHighlightingDefinition
		{
			private readonly object lockObj = new();
			// name is null when the caller wants the name read from the definition itself, which
			// means loading it. The other three are the load state: the function is dropped once
			// it has run, and exactly one of definition/storedException is set afterwards.
			private readonly string? name;
			private Func<IHighlightingDefinition>? lazyLoadingFunction;
			private IHighlightingDefinition? definition;
			private Exception? storedException;

			public DelayLoadedHighlightingDefinition(string? name, Func<IHighlightingDefinition> lazyLoadingFunction)
			{
				this.name = name;
				this.lazyLoadingFunction = lazyLoadingFunction;
			}

			public string? Name {
				get {
					if (name != null) {
						return name;
					} else {
						return GetDefinition().Name;
					}
				}
			}

			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes",
															 Justification = "The exception will be rethrown")]
			private IHighlightingDefinition GetDefinition()
			{
				Func<IHighlightingDefinition> func;
				lock (lockObj) {
					if (this.definition != null) {
						return this.definition;
					}

					// Not null here: it is only cleared after definition or storedException has
					// been set, and both of those paths return or throw before reaching this.
					func = this.lazyLoadingFunction!;
				}
				Exception? exception = null;
				IHighlightingDefinition? def = null;
				try {
					using (BusyManager.BusyLock busyLock = BusyManager.Enter(this)) {
						if (!busyLock.Success) {
							throw new InvalidOperationException("Tried to create delay-loaded highlighting definition recursively. Make sure the are no cyclic references between the highlighting definitions.");
						}

						def = func();
					}
					if (def == null) {
						throw new InvalidOperationException("Function for delay-loading highlighting definition returned null");
					}
				} catch (Exception ex) {
					exception = ex;
				}
				lock (lockObj) {
					this.lazyLoadingFunction = null;
					if (this.definition == null && this.storedException == null) {
						this.definition = def;
						this.storedException = exception;
					}
					if (this.storedException != null) {
						throw new HighlightingDefinitionInvalidException("Error delay-loading highlighting definition", this.storedException);
					}

					// No stored exception means the load produced a definition.
					return this.definition!;
				}
			}

			public HighlightingRuleSet MainRuleSet => GetDefinition().MainRuleSet;

			public HighlightingRuleSet? GetNamedRuleSet(string name)
			{
				return GetDefinition().GetNamedRuleSet(name);
			}

			public HighlightingColor? GetNamedColor(string name)
			{
				return GetDefinition().GetNamedColor(name);
			}

			public IEnumerable<HighlightingColor> NamedHighlightingColors => GetDefinition().NamedHighlightingColors;

			public override string ToString()
			{
				return this.Name ?? string.Empty;
			}

			public IDictionary<string, string> Properties => GetDefinition().Properties;
		}

		private readonly object lockObj = new();
		private readonly Dictionary<string, IHighlightingDefinition> highlightingsByName = [];
		private readonly Dictionary<string, IHighlightingDefinition> highlightingsByExtension = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<IHighlightingDefinition> allHighlightings = [];

		/// <summary>
		/// Gets a highlighting definition by name.
		/// Returns null if the definition is not found.
		/// </summary>
		public IHighlightingDefinition? GetDefinition(string name)
		{
			lock (lockObj) {
				if (highlightingsByName.TryGetValue(name, out IHighlightingDefinition rh)) {
					return rh;
				} else {
					return null;
				}
			}
		}

		/// <summary>
		/// Gets a copy of all highlightings.
		/// </summary>
		public ReadOnlyCollection<IHighlightingDefinition> HighlightingDefinitions {
			get {
				lock (lockObj) {
					return Array.AsReadOnly(allHighlightings.ToArray());
				}
			}
		}

		/// <summary>
		/// Gets a highlighting definition by extension.
		/// Returns null if the definition is not found.
		/// </summary>
		public IHighlightingDefinition? GetDefinitionByExtension(string extension)
		{
			lock (lockObj) {
				if (highlightingsByExtension.TryGetValue(extension, out IHighlightingDefinition rh)) {
					return rh;
				} else {
					return null;
				}
			}
		}

		/// <summary>
		/// Registers a highlighting definition.
		/// </summary>
		/// <param name="name">The name to register the definition with.</param>
		/// <param name="extensions">The file extensions to register the definition for.</param>
		/// <param name="highlighting">The highlighting definition.</param>
		// name and extensions are both optional - a definition can be registered under neither,
		// which is what the null checks in the body are for.
		public void RegisterHighlighting(string? name, string[]? extensions, IHighlightingDefinition highlighting)
		{
			if (highlighting == null) {
				throw new ArgumentNullException("highlighting");
			}

			lock (lockObj) {
				if (name != null) {
					if (highlightingsByName.TryGetValue(name, out IHighlightingDefinition? existingDefinition)) {
						allHighlightings.Remove(existingDefinition);
					}

					highlightingsByName[name] = highlighting;
				}
				if (extensions != null) {
					foreach (string ext in extensions) {
						highlightingsByExtension[ext] = highlighting;
					}
				}
				allHighlightings.Add(highlighting);
			}
		}

		/// <summary>
		/// Registers a highlighting definition.
		/// </summary>
		/// <param name="name">The name to register the definition with.</param>
		/// <param name="extensions">The file extensions to register the definition for.</param>
		/// <param name="lazyLoadedHighlighting">A function that loads the highlighting definition.</param>
		public void RegisterHighlighting(string? name, string[]? extensions, Func<IHighlightingDefinition> lazyLoadedHighlighting)
		{
			if (lazyLoadedHighlighting == null) {
				throw new ArgumentNullException("lazyLoadedHighlighting");
			}

			RegisterHighlighting(name, extensions, new DelayLoadedHighlightingDefinition(name, lazyLoadedHighlighting));
		}

		/// <summary>
		/// Gets the default HighlightingManager instance.
		/// The default HighlightingManager comes with built-in highlightings.
		/// </summary>
		public static HighlightingManager Instance => DefaultHighlightingManager.Instance;

		internal sealed class DefaultHighlightingManager : HighlightingManager
		{
			public static new readonly DefaultHighlightingManager Instance = new();

			public DefaultHighlightingManager()
			{
				Resources.RegisterBuiltInHighlightings(this);
			}

			// Registering a built-in highlighting
			internal void RegisterHighlighting(string? name, string[]? extensions, string resourceName)
			{
				try {
#if DEBUG
					// don't use lazy-loading in debug builds, show errors immediately
					Xshd.XshdSyntaxDefinition xshd;
					using (Stream s = Resources.OpenStream(resourceName)) {
						using XmlTextReader reader = new(s);
						xshd = Xshd.HighlightingLoader.LoadXshd(reader, false);
					}
					Debug.Assert(name == xshd.Name);
					if (extensions != null) {
						Debug.Assert(System.Linq.Enumerable.SequenceEqual(extensions, xshd.Extensions));
					} else {
						Debug.Assert(xshd.Extensions.Count == 0);
					}

					// round-trip xshd:
					//					string resourceFileName = Path.Combine(Path.GetTempPath(), resourceName);
					//					using (XmlTextWriter writer = new XmlTextWriter(resourceFileName, System.Text.Encoding.UTF8)) {
					//						writer.Formatting = Formatting.Indented;
					//						new Xshd.SaveXshdVisitor(writer).WriteDefinition(xshd);
					//					}
					//					using (FileStream fs = File.Create(resourceFileName + ".bin")) {
					//						new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(fs, xshd);
					//					}
					//					using (FileStream fs = File.Create(resourceFileName + ".compiled")) {
					//						new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(fs, Xshd.HighlightingLoader.Load(xshd, this));
					//					}

					RegisterHighlighting(name, extensions, Xshd.HighlightingLoader.Load(xshd, this));
#else
					RegisterHighlighting(name, extensions, LoadHighlighting(resourceName));
#endif
				} catch (HighlightingDefinitionInvalidException ex) {
					throw new InvalidOperationException("The built-in highlighting '" + name + "' is invalid.", ex);
				}
			}

			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode",
															 Justification = "LoadHighlighting is used only in release builds")]
			private Func<IHighlightingDefinition> LoadHighlighting(string resourceName)
			{
				IHighlightingDefinition func()
				{
					Xshd.XshdSyntaxDefinition xshd;
					using (Stream s = Resources.OpenStream(resourceName)) {
						using XmlTextReader reader = new(s);
						// in release builds, skip validating the built-in highlightings
						xshd = Xshd.HighlightingLoader.LoadXshd(reader, true);
					}
					return Xshd.HighlightingLoader.Load(xshd, this);
				}
				return func;
			}
		}
	}
}
