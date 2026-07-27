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

using System.IO;

namespace ICSharpCode.AvalonEdit.Highlighting
{
	internal static class Resources
	{
		private static readonly string Prefix = typeof(Resources).FullName + ".";

		public static Stream OpenStream(string name)
		{
			Stream s = typeof(Resources).Assembly.GetManifestResourceStream(Prefix + name) ?? throw new FileNotFoundException("The resource file '" + name + "' was not found.");
			return s;
		}

		internal static void RegisterBuiltInHighlightings(HighlightingManager.DefaultHighlightingManager hlm)
		{
			hlm.RegisterHighlighting("XmlDoc", null, "XmlDoc.xshd");
			hlm.RegisterHighlighting("C#", [".cs"], "CSharp-Mode.xshd");

			hlm.RegisterHighlighting("JavaScript", [".js"], "JavaScript-Mode.xshd");
			hlm.RegisterHighlighting("HTML", [".htm", ".html"], "HTML-Mode.xshd");
			hlm.RegisterHighlighting("ASP/XHTML", [".asp", ".aspx", ".asax", ".asmx", ".ascx", ".master"], "ASPX.xshd");

			hlm.RegisterHighlighting("Boo", [".boo"], "Boo.xshd");
			hlm.RegisterHighlighting("Coco", [".atg"], "Coco-Mode.xshd");
			hlm.RegisterHighlighting("CSS", [".css"], "CSS-Mode.xshd");
			hlm.RegisterHighlighting("C++", [".c", ".h", ".cc", ".cpp", ".hpp"], "CPP-Mode.xshd");
			hlm.RegisterHighlighting("Java", [".java"], "Java-Mode.xshd");
			hlm.RegisterHighlighting("Patch", [".patch", ".diff"], "Patch-Mode.xshd");
			hlm.RegisterHighlighting("PowerShell", [".ps1", ".psm1", ".psd1"], "PowerShell.xshd");
			hlm.RegisterHighlighting("PHP", [".php"], "PHP-Mode.xshd");
			hlm.RegisterHighlighting("Python", [".py", ".pyw"], "Python-Mode.xshd");
			hlm.RegisterHighlighting("TeX", [".tex"], "Tex-Mode.xshd");
			hlm.RegisterHighlighting("TSQL", [".sql"], "TSQL-Mode.xshd");
			hlm.RegisterHighlighting("VB", [".vb"], "VB-Mode.xshd");
			hlm.RegisterHighlighting("XML", (".xml;.xsl;.xslt;.xsd;.manifest;.config;.addin;" +
											 ".xshd;.wxs;.wxi;.wxl;.proj;.csproj;.vbproj;.ilproj;" +
											 ".booproj;.build;.xfrm;.targets;.xaml;.xpt;" +
											 ".xft;.map;.wsdl;.disco;.ps1xml;.nuspec").Split(';'),
									 "XML-Mode.xshd");
			hlm.RegisterHighlighting("MarkDown", [".md"], "MarkDown-Mode.xshd");
			hlm.RegisterHighlighting("MarkDownWithFontSize", [".md"], "MarkDownWithFontSize-Mode.xshd");
			hlm.RegisterHighlighting("Json", [".json"], "Json.xshd");
		}
	}
}
