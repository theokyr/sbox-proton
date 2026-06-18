using Editor;

namespace Tools;

[TestClass]
public class AssetSystemTest
{
	[TestMethod]
	public void GetCloudDatabasePath_NormalizesWineUnixRootPath()
	{
		var path = AssetSystem.GetCloudDatabasePath( "S:\\home\\theo\\src\\sbox\\ultraneon" );

		Assert.AreEqual( "/home/theo/src/sbox/ultraneon/.sbox/cloud.db", path );
	}
}
