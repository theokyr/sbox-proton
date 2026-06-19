using Editor;
using System;

namespace Tools;

[TestClass]
public class AssetSystemTest
{
	[TestMethod]
	public void GetCloudDatabasePath_UsesManagedWinePathForCloudDatabase()
	{
		var tempRoot = System.IO.Path.Combine( System.IO.Path.GetTempPath(), $"sbox-assetsystem-{Guid.NewGuid():N}" );
		var prefix = System.IO.Path.Combine( tempRoot, "compatdata", "2129370" );

		try
		{
			System.IO.Directory.CreateDirectory( System.IO.Path.Combine( prefix, "pfx", "drive_c" ) );

			var path = AssetSystem.GetCloudDatabasePath( "S:\\home\\devuser\\src\\sbox\\proton_test", prefix );

			Assert.AreEqual( "Z:/home/devuser/src/sbox/proton_test/.sbox/cloud.db", path );
		}
		finally
		{
			if ( System.IO.Directory.Exists( tempRoot ) )
			{
				System.IO.Directory.Delete( tempRoot, true );
			}
		}
	}

	[TestMethod]
	public void NativeAssetAbsolutePathNormalizationPreservesSourceCasing()
	{
		var path = NativeAsset.NormalizeAbsoluteAssetPathForHost( "Z:\\home\\devuser\\src\\sbox\\sample_project\\Assets\\Sounds\\Music\\Arena\\arena.music" );

		Assert.AreEqual( "/home/devuser/src/sbox/sample_project/Assets/Sounds/Music/Arena/arena.music", path );
	}
}
