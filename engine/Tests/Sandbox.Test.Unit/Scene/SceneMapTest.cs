namespace Scene;

[TestClass]
public class SceneMapTest
{
	[DataTestMethod]
	[DataRow( "sp_cp_compound.vpk", "sp_cp_compound", "sp_cp_compound.vpk" )]
	[DataRow( "maps/sp_cp_compound.vpk", "sp_cp_compound", "sp_cp_compound.vpk" )]
	[DataRow( "maps/sp_cp_compound.vmap", "sp_cp_compound", "sp_cp_compound.vpk" )]
	[DataRow( "\\maps\\sp_cp_compound.vmap", "sp_cp_compound", "sp_cp_compound.vpk" )]
	public void NormalizeMapReference_RemovesLeadingMapsFolderForNativeVpk( string input, string expectedMapName, string expectedVpkPath )
	{
		var reference = SceneMap.NormalizeMapReference( input );

		Assert.AreEqual( expectedMapName, reference.MapName );
		Assert.AreEqual( expectedVpkPath, reference.NativeVpkPath );
	}
}
