using Editor;

namespace Tools;

[TestClass]
public class SboxMcpBridgeTest
{
	[TestMethod]
	public void TryParseHomeFromLinuxPath_AcceptsExactHomeDirectory()
	{
		Assert.AreEqual( "/home/theo", SboxMcpBridge.TryParseHomeFromLinuxPath( "/home/theo" ) );
	}

	[TestMethod]
	public void TryParseHomeFromLinuxPath_ParsesHomeFromNestedPath()
	{
		Assert.AreEqual( "/home/theo", SboxMcpBridge.TryParseHomeFromLinuxPath( "/home/theo/src/sbox/ultraneon" ) );
	}
}
