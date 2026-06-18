using Editor;

namespace Tools;

[TestClass]
public class AssetSystemTest
{
	[TestMethod]
	public void GetCloudDatabasePath_NormalizesWineUnixRootPath()
	{
		var path = AssetSystem.GetCloudDatabasePath( "S:\\home\\devuser\\src\\sbox\\proton_test" );

		Assert.AreEqual( "/home/devuser/src/sbox/proton_test/.sbox/cloud.db", path );
	}
}
