using System;
using System.IO;
using Cardinator.Services;

namespace Cardinator.Tests;

public class IoUtilTests
{
    [Fact]
    public void AtomicWriteText_WritesContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cardinator_io_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "file.txt");   // also exercises directory creation
        try
        {
            IoUtil.AtomicWriteText(path, "hello");
            Assert.Equal("hello", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void AtomicWrite_Failure_LeavesOriginalIntact_AndNoTempFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cardinator_io_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "file.txt");
        try
        {
            IoUtil.AtomicWriteText(path, "original");

            Assert.Throws<InvalidOperationException>(() =>
                IoUtil.AtomicWrite(path, tmp =>
                {
                    File.WriteAllText(tmp, "partial");
                    throw new InvalidOperationException("boom");   // simulate a mid-write failure
                }));

            Assert.Equal("original", File.ReadAllText(path));      // untouched
            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));      // temp cleaned up
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
