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

	[TestMethod]
	public void NativeAssetAbsolutePathNormalizationPreservesSourceCasing()
	{
		var path = NativeAsset.NormalizeAbsoluteAssetPathForHost( "Z:\\home\\devuser\\src\\sbox\\ultraneon\\Assets\\Sounds\\Music\\Compound\\compound.music" );

		Assert.AreEqual( "/home/devuser/src/sbox/ultraneon/Assets/Sounds/Music/Compound/compound.music", path );
	}
}
