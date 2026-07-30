using System.Threading;
using System.Threading.Tasks;
using Axiom.Core.Agent;
using Xunit;

namespace Axiom.Core.Tests.Agent
{
    public class DeviceInfoToolsTests
    {
        [Fact]
        public async Task ExecuteAsync_DeviceInfo_ReturnsOsAndRuntimeDetails()
        {
            var workspace = new WorkspaceSession(attachCwd: false);
            var executor = new AgentToolExecutor(workspace);

            string result = await executor.ExecuteAsync("device_info", "{}", CancellationToken.None);

            Assert.Contains("OS:", result);
            Assert.Contains(".NET runtime:", result);
            Assert.Contains("Logical processors:", result);
            Assert.DoesNotContain("Tool error", result);
        }

        [Fact]
        public async Task ExecuteAsync_ListSerialPorts_DoesNotThrowAndReturnsAReport()
        {
            var workspace = new WorkspaceSession(attachCwd: false);
            var executor = new AgentToolExecutor(workspace);

            string result = await executor.ExecuteAsync("list_serial_ports", "{}", CancellationToken.None);

            // Machine-dependent whether any ports exist -- just assert it reports one way or the
            // other instead of throwing/erroring.
            Assert.DoesNotContain("Tool error", result);
            Assert.True(result.Contains("No serial/COM ports detected.") || result.Contains("serial port(s) detected"));
        }

        [Fact]
        public void GetToolDefinitions_IncludesDeviceIntrospectionTools()
        {
            var workspace = new WorkspaceSession(attachCwd: false);
            var executor = new AgentToolExecutor(workspace);

            var tools = executor.GetToolDefinitions();

            Assert.Contains(tools, t => t.Name == "device_info");
            Assert.Contains(tools, t => t.Name == "list_serial_ports");
        }
    }
}
