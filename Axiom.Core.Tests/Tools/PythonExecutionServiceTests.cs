using System.Threading.Tasks;
using Axiom.Core.Tools;
using Xunit;

namespace Axiom.Core.Tests.Tools
{
    public class PythonExecutionServiceTests
    {
        [Fact]
        public async Task ExecuteMathScriptAsync_RunsAgainstSystemPython()
        {
            var service = new PythonExecutionService();
            // The production default (10s) is sized for interactive tool use; under xUnit's
            // parallel test execution, pythonnet's first-time engine init competes with other
            // test classes for CPU and can miss that window without being genuinely broken.
            PythonExecutionResult result = await service.ExecuteMathScriptAsync("print(2 + 2)", timeoutMs: 30000);

            Assert.True(result.Success, result.Output);
            Assert.Contains("4", result.Output);
        }
    }
}
