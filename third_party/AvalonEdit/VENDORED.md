# AvalonEdit, vendored

Source: https://github.com/icsharpcode/AvalonEdit, tag **v6.3.1** (released 2025-04-13, and still
the newest release upstream has cut). No security advisories are published against this project.
License: MIT. The upstream LICENSE and ChangeLog.md sit beside this file.
Taken: 2026-07-26. Only the `ICSharpCode.AvalonEdit` project folder came across; the samples,
tests, documentation and build tooling in that repo did not.

## Why the source and not a package

KillerShell ships as one portable exe with nothing loose beside it. A referenced assembly would
mean either a DLL on disk or a weaver embedding it into the exe, and the source compiled straight
in keeps the single file while leaving every line the exe contains readable in this repo. Same
call that was made for PdfSharpCore in KillerPDF.

## Local modifications

Keep this list current. An upgrade is a fresh extract of the new tag with these applied again.

1. `themes/generic.xaml`: the six `/ICSharpCode.AvalonEdit;component/...` URIs are now
   `/KillerShell;component/third_party/AvalonEdit/...`. The source is compiled into KillerShell.exe,
   so at runtime there is no ICSharpCode.AvalonEdit assembly for the original URIs to name.

2. **Nullable annotation pass.** The tree was written years before nullable reference types and
   arrived with 2142 warnings under KillerShell's `<Nullable>enable</Nullable>`. Every one is being
   annotated rather than suppressed: `?` where null is real and the code already tests for it,
   `= null!` where a field is genuinely assigned before any caller can see it, and a real fix
   wherever the compiler found an actual hole. Note that the count does not fall linearly:
   annotating a type correctly surfaces every call site that was quietly assuming it non-null,
   so a folder often nets close to zero on its first pass and collapses on the second.
   Folders finished so far: **Indentation, Search, Snippets, CodeCompletion, Folding, Utils, and
   the root files**. Utils was the hard one: `CompressingTreeList` is a red-black tree and
   `Rope`/`RopeNode` a rope, and in both the shape of a node is expressed purely through which
   fields are null. Every `!` in those files carries a comment naming the invariant that makes it
   safe, usually one upstream had already written in prose directly above it.

3. **Whitespace normalized to their own convention.** `third_party/.editorconfig` declares the
   style this tree is actually written in (tabs, LF, Allman on types and methods only), which is
   what lets IDE0055 measure it against its own standard instead of the .NET default. 19 files
   were inconsistent with that convention and were normalized by `dotnet format whitespace`;
   the other 195 were already correct and were not touched. The tree was deliberately NOT
   reformatted to KillerShell's own four-space style: keeping upstream's formatting is what lets
   a fresh extract of the next release be diffed against this copy to see only the changes in
   this list.

4. `Rendering/GlobalTextRunProperties.cs`: dropped the internal `backgroundBrush` field. Nothing
   in the assembly ever assigned it (CS0649), so `BackgroundBrush` could only ever return null;
   it now returns null outright, the shape `TextDecorations` and `TextEffects` already used.

5. `Search/SearchPanel.cs`: `UpdateSearch` passed the nullable `SearchPattern` dependency
   property straight into `SearchOptionsChangedEventArgs`, whose parameter is non-null, one line
   after guarding the same value with `?? ""` for the strategy factory. Now guarded in both
   places. A search panel raising the event with an unset pattern would have thrown.

6. `Search/SearchResultBackgroundRenderer.cs`: `MarkerPen` is `Pen?`. The constructor sets it to
   null and `Draw` tests it for null, so the unannotated `Pen` was a promise the class never kept.

7. `Indentation/CSharp/IndentationReformatter.cs`: `wordBuilder` and `blocks` are initialized at
   their declaration. `Init()` assigned them and `Reformat` calls `Init` first, but `Step` is
   public and reachable without it, which was a null dereference waiting for a caller.

## The style audit

The IDE analyzers were turned up to warning for this tree (`third_party/.editorconfig`) so that
nothing could hide at suggestion severity, which is where Visual Studio's blue squiggles live and
where MSBuild never looks. That surfaced 129,102 findings. They came down like this:

| pass | findings left |
|---|---|
| starting point, everything elevated | 129,102 |
| declared their formatting convention, LF and tabs | 16,530 |
| `dotnet format whitespace`, 19 inconsistent files | 16,530 |
| `dotnet format style`, braces and accessibility modifiers | 6,501 |
| `dotnet format style`, expression bodies, `var`, conditional delegate calls | 4,074 |
| remaining mechanical rules | 3,252 |
| audited exceptions recorded below | 1,632 |

Everything mechanical was fixed, not muted. Seven rules were turned down, each read site by site
first and each recorded with its reasoning in `third_party/.editorconfig`: IDE0058 (240 sites, all
idiomatic discards, no defect found), IDE0046 and IDE0045, IDE0290, IDE1006, IDE0010, IDE0060 and
IDE0130. Two findings from that audit are worth knowing even though nothing was changed:

- `Rendering/TextView.cs` `InvalidateLayer(KnownLayer)` ignores its parameter, so it invalidates
  every layer whatever you pass it. Upstream behavior, left alone deliberately.
- `Folding/XmlFoldingStrategy.XmlEncodeAttributeValue` calls `Replace` five times and discards
  every result, which would be a silent no-op on a `string`. It is a `StringBuilder`, so it is
  correct. Worth re-checking if upstream ever changes that local.

## How it is wired into the build

`KillerShell.csproj` removes `third_party\**` from every default glob and then adds it back
deliberately, because the SDK would otherwise sweep the whole tree into the compile:

- **Compile**: every `.cs` except `Properties\AssemblyInfo.cs`, whose assembly attributes would
  collide with KillerShell's own.
- **Page**: every `.xaml`. `themes\generic.xaml` carries a `Link` back to `themes\generic.xaml`
  so WPF finds the default styles where it looks for them; KillerShell's AssemblyInfo already
  declares `ThemeInfo(..., ResourceDictionaryLocation.SourceAssembly)`.
- **EmbeddedResource**: `Highlighting\Resources\*` with an explicit `LogicalName` of
  `ICSharpCode.AvalonEdit.Highlighting.Resources.<file>`. Resources.cs resolves the built-in
  highlightings by that exact string (`typeof(Resources).FullName + "."`), and the name MSBuild
  would infer here starts with `KillerShell.third_party` instead, so every built-in definition
  would come back null.
- **Resource**: `Search\next.png`, `Search\prev.png` and `themes\RightArrow.cur`, all referenced
  from the XAML.

Nothing here is suppressed. This tree builds warning-clean and message-clean under KillerShell's
own settings, nullable included, and every change made to get there is listed under Local
modifications. An upgrade is therefore a merge rather than a re-extract, which is the deliberate
trade: the vendored copy is held to the same bar as the rest of the repo.

## Upgrading

Extract the new tag's `ICSharpCode.AvalonEdit` folder over this one, reapply the modifications
above, then check whether upstream added files under `Highlighting\Resources` or new `.xaml`
(both are wildcarded in the csproj, so they come in on their own) and whether anything new needs
a `Resource` entry.
