using Editor;

namespace Tools;

[TestClass]
public class StartupLoadProjectTest
{
	[TestMethod]
	public void GetNativeResourceMounts_RegistersProjectCloudAsProjectPath()
	{
		var root = "/home/theo/src/sbox/ultraneon";
		var mounts = StartupLoadProject.GetNativeResourceMounts(
			"ktl.ultraneon",
			"ultraneon",
			root,
			$"{root}/Assets",
			"/home/theo/.local/share/Steam/steamapps/common/sbox/addons/menu/transients" ).ToArray();

		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "ktl.ultraneon", null, $"{root}/Assets", true ) );
		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "ktl.ultraneon.cloud", "mod_cloud", $"{root}/.sbox/cloud", true ) );
		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "ktl.ultraneon.transient", "mod_transient", $"{root}/.sbox/transient", true ) );
		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "local.sbox_engine_transient", "mod_engtrans", "/home/theo/.local/share/Steam/steamapps/common/sbox/addons/menu/transients", false ) );
	}

	[TestMethod]
	public void GetNativeResourceMounts_SkipsEngineTransientForMenuProject()
	{
		var root = "/home/theo/src/extern/sbox-public/game/addons/menu";
		var mounts = StartupLoadProject.GetNativeResourceMounts(
			"local.menu",
			"menu",
			root,
			$"{root}/Assets",
			"/home/theo/.local/share/Steam/steamapps/common/sbox/addons/menu/transients" ).ToArray();

		Assert.IsFalse( mounts.Any( x => x.CloudIdent == "mod_engtrans" ) );
	}
}
