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

namespace ICSharpCode.AvalonEdit.Highlighting.Xshd
{
	/// <summary>
	/// A reference to an xshd color, or an inline xshd color.
	/// </summary>
	[Serializable]
	public readonly struct XshdReference<T> : IEquatable<XshdReference<T>> where T : XshdElement
	{
		// A reference is EITHER a name pair or an inline element, never both, so whichever half is
		// not in use is null. The default struct value has all three null and means "no color".
		private readonly string? referencedDefinition;
		private readonly string? referencedElement;
		private readonly T? inlineElement;

		/// <summary>
		/// Gets the reference.
		/// </summary>
		public readonly string? ReferencedDefinition => referencedDefinition;

		/// <summary>
		/// Gets the reference.
		/// </summary>
		public readonly string? ReferencedElement => referencedElement;

		/// <summary>
		/// Gets the inline element.
		/// </summary>
		public readonly T? InlineElement => inlineElement;

		/// <summary>
		/// Creates a new XshdReference instance.
		/// </summary>
		public XshdReference(string? referencedDefinition, string referencedElement)
		{
			this.referencedDefinition = referencedDefinition;
			this.referencedElement = referencedElement ?? throw new ArgumentNullException("referencedElement");
			this.inlineElement = null;
		}

		/// <summary>
		/// Creates a new XshdReference instance.
		/// </summary>
		public XshdReference(T inlineElement)
		{
			this.referencedDefinition = null;
			this.referencedElement = null;
			this.inlineElement = inlineElement ?? throw new ArgumentNullException("inlineElement");
		}

		/// <summary>
		/// Applies the visitor to the inline element, if there is any.
		/// </summary>
		public readonly object? AcceptVisitor(IXshdVisitor visitor)
		{
			if (inlineElement != null) {
				return inlineElement.AcceptVisitor(visitor);
			} else {
				return null;
			}
		}

		#region Equals and GetHashCode implementation
		// The code in this region is useful if you want to use this structure in collections.
		// If you don't need it, you can just remove the region and the ": IEquatable<XshdColorReference>" declaration.

		/// <inheritdoc/>
		public override bool Equals(object? obj)
		{
			if (obj is XshdReference<T>) {
				return Equals((XshdReference<T>)obj); // use Equals method below
			} else {
				return false;
			}
		}

		/// <summary>
		/// Equality operator.
		/// </summary>
		public readonly bool Equals(XshdReference<T> other)
		{
			// add comparisions for all members here
			return this.referencedDefinition == other.referencedDefinition
				&& this.referencedElement == other.referencedElement
				&& this.inlineElement == other.inlineElement;
		}

		/// <inheritdoc/>
		public override readonly int GetHashCode()
		{
			// combine the hash codes of all members here (e.g. with XOR operator ^)
			return GetHashCode(referencedDefinition) ^ GetHashCode(referencedElement) ^ GetHashCode(inlineElement);
		}

		private static int GetHashCode(object? o)
		{
			return o != null ? o.GetHashCode() : 0;
		}

		/// <summary>
		/// Equality operator.
		/// </summary>
		public static bool operator ==(XshdReference<T> left, XshdReference<T> right)
		{
			return left.Equals(right);
		}

		/// <summary>
		/// Inequality operator.
		/// </summary>
		public static bool operator !=(XshdReference<T> left, XshdReference<T> right)
		{
			return !left.Equals(right);
		}
		#endregion
	}
}
