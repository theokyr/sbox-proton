namespace Scene;

[TestClass]
public class SceneMapTest
	{
		[DataTestMethod]
		[DataRow( "example_arena.vpk", "example_arena", "example_arena.vpk" )]
		[DataRow( "maps/example_arena.vpk", "example_arena", "example_arena.vpk" )]
		[DataRow( "maps/example_arena.vmap", "example_arena", "example_arena.vpk" )]
		[DataRow( "\\maps\\example_arena.vmap", "example_arena", "example_arena.vpk" )]
		public void NormalizeMapReference_RemovesLeadingMapsFolderForNativeVpk( string input, string expectedMapName, string expectedVpkPath )
		{
			var reference = SceneMap.NormalizeMapReference( input );

		Assert.AreEqual( expectedMapName, reference.MapName );
		Assert.AreEqual( expectedVpkPath, reference.NativeVpkPath );
	}
}
