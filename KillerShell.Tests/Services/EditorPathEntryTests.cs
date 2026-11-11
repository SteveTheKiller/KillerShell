using System;
using System.Collections.Generic;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class EditorPathEntryTests
    {
        private const string Current = @"C:\proj\app.config";
        private const string Folder = @"C:\proj";

        private static Func<string, bool> Files(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        private static Func<string, bool> Folders(params string[] paths)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            return set.Contains;
        }

        [Fact]
        public void AnotherFileThatExistsOpens()
        {
            var r = EditorPathEntry.Resolve(@"C:\proj\.env", Current, Folder, Files(@"C:\proj\.env"), Folders(Folder));
            Assert.Equal(EditorPathAction.Open, r.Action);
            Assert.Equal(@"C:\proj\.env", r.Path);
        }

        [Fact]
        public void ABareNameOpensFromTheDocumentsFolder()
        {
            var r = EditorPathEntry.Resolve(".env", Current, Folder, Files(@"C:\proj\.env"), Folders(Folder));
            Assert.Equal(EditorPathAction.Open, r.Action);
            Assert.Equal(@"C:\proj\.env", r.Path);
        }

        [Fact]
        public void AQuotedPathIsUnquoted()
        {
            var r = EditorPathEntry.Resolve("\"C:\\proj\\.env\"", Current, Folder, Files(@"C:\proj\.env"), Folders(Folder));
            Assert.Equal(EditorPathAction.Open, r.Action);
            Assert.Equal(@"C:\proj\.env", r.Path);
        }

        [Fact]
        public void ANewNameRenamesInTheSameFolder()
        {
            var r = EditorPathEntry.Resolve("app.json", Current, Folder, Files(Current), Folders(Folder));
            Assert.Equal(EditorPathAction.Rename, r.Action);
            Assert.Equal(@"C:\proj\app.json", r.Path);
        }

        [Fact]
        public void TheSamePathIsUnchanged()
        {
            var r = EditorPathEntry.Resolve(Current, Current, Folder, Files(Current), Folders(Folder));
            Assert.Equal(EditorPathAction.Unchanged, r.Action);
        }

        [Fact]
        public void ACaseOnlyChangeRenamesInsteadOfOpeningItself()
        {
            var r = EditorPathEntry.Resolve(@"C:\proj\App.config", Current, Folder, Files(Current), Folders(Folder));
            Assert.Equal(EditorPathAction.Rename, r.Action);
            Assert.Equal(@"C:\proj\App.config", r.Path);
        }

        [Fact]
        public void AMissingFolderIsNotFound()
        {
            var r = EditorPathEntry.Resolve(@"C:\nope\x.txt", Current, Folder, Files(Current), Folders(Folder));
            Assert.Equal(EditorPathAction.NotFound, r.Action);
        }

        [Fact]
        public void AFolderIsNotARenameTarget()
        {
            var r = EditorPathEntry.Resolve(@"C:\proj\sub", Current, Folder, Files(Current), Folders(Folder, @"C:\proj\sub"));
            Assert.Equal(EditorPathAction.Exists, r.Action);
        }

        [Fact]
        public void AnUntitledDocumentOpensAnExistingFile()
        {
            var r = EditorPathEntry.Resolve(".env", string.Empty, Folder, Files(@"C:\proj\.env"), Folders(Folder));
            Assert.Equal(EditorPathAction.Open, r.Action);
            Assert.Equal(@"C:\proj\.env", r.Path);
        }

        [Fact]
        public void AnUntitledDocumentSavesToANewName()
        {
            var r = EditorPathEntry.Resolve("notes.txt", string.Empty, Folder, Files(), Folders(Folder));
            Assert.Equal(EditorPathAction.Rename, r.Action);
            Assert.Equal(Path.Combine(Folder, "notes.txt"), r.Path);
        }

        [Theory]
        [InlineData(true, false, 0, true)]
        [InlineData(true, false, 12, false)]
        [InlineData(true, true, 0, false)]
        [InlineData(false, false, 0, false)]
        public void OnlyABlankUntitledTabIsReused(bool untitled, bool dirty, int textLength, bool reused)
            => Assert.Equal(reused, EditorPathEntry.ReusesTab(untitled, dirty, textLength));
    }
}
