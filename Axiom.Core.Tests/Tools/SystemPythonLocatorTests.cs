using Axiom.Core.Tools;
using Xunit;

namespace Axiom.Core.Tests.Tools
{
    public class SystemPythonLocatorTests
    {
        [Fact]
        public void TryLocate_FindsAUsableSystemPythonOnThisMachine()
        {
            bool found = SystemPythonLocator.TryLocate(out PythonRuntimeInfo? info, out string error);

            Assert.True(found, $"Expected a system Python to be discovered. Error: {error}");
            Assert.NotNull(info);
            Assert.True(System.IO.File.Exists(info!.ExecutablePath));
            Assert.True(System.IO.File.Exists(info.SharedLibraryPath));
        }
    }
}
