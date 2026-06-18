using Editor;

namespace Tools;

[TestClass]
public class StartupLoadProjectTest
{
	[TestMethod]
	public void GetNativeResourceMounts_RegistersProjectCloudAsProjectPath()
	{
		var root = "/home/devuser/src/sbox/proton_test";
		var mounts = StartupLoadProject.GetNativeResourceMounts(
			"local.proton_test",
			"proton_test",
			root,
			$"{root}/Assets",
			"/home/steamuser/.local/share/Steam/steamapps/common/sbox/addons/menu/transients" ).ToArray();

		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "local.proton_test", null, $"{root}/Assets", true ) );
		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "local.proton_test.cloud", "mod_cloud", $"{root}/.sbox/cloud", true ) );
		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "local.proton_test.transient", "mod_transient", $"{root}/.sbox/transient", true ) );
		CollectionAssert.Contains( mounts, new StartupLoadProject.NativeResourceMount( "local.sbox_engine_transient", "mod_engtrans", "/home/steamuser/.local/share/Steam/steamapps/common/sbox/addons/menu/transients", false ) );
	}

	[TestMethod]
	public void GetNativeResourceMounts_SkipsEngineTransientForMenuProject()
	{
		var root = "/home/devuser/src/sbox-public/game/addons/menu";
		var mounts = StartupLoadProject.GetNativeResourceMounts(
			"local.menu",
			"menu",
			root,
			$"{root}/Assets",
			"/home/steamuser/.local/share/Steam/steamapps/common/sbox/addons/menu/transients" ).ToArray();

		Assert.IsFalse( mounts.Any( x => x.CloudIdent == "mod_engtrans" ) );
	}
}
