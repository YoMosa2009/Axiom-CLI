using System;
using System.IO;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class FileChangeUndoTests
    {
        [Fact]
        public void UndoLast_RestoresPreviousContent()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-undo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "hello");
            try
            {
                var undo = new FileChangeUndo();
                undo.BeginTurn("t1");
                undo.RecordBeforeWrite(path);
                File.WriteAllText(path, "world");
                undo.CommitTurn();

                Assert.True(undo.CanUndo);
                string summary = undo.UndoLast();
                Assert.Contains("restored", summary, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("hello", File.ReadAllText(path));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void UndoLast_DeletesCreatedFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "axiom-undo2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "new.txt");
            try
            {
                var undo = new FileChangeUndo();
                undo.BeginTurn("create");
                undo.RecordBeforeWrite(path);
                File.WriteAllText(path, "brand new");
                undo.CommitTurn();

                undo.UndoLast();
                Assert.False(File.Exists(path));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* ignore */ }
            }
        }
    }
}
