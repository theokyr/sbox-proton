using Editor;

namespace Tools;

[TestClass]
public class WindowControlButtonTest
{
	[TestMethod]
	public void WindowControlsUseMaterialIconLigatures()
	{
		Assert.AreEqual( "remove", WindowControlButton.GetMaterialIconName( WindowControlIcon.Minimize ) );
		Assert.AreEqual( "crop_square", WindowControlButton.GetMaterialIconName( WindowControlIcon.Maximize ) );
		Assert.AreEqual( "filter_none", WindowControlButton.GetMaterialIconName( WindowControlIcon.Restore ) );
		Assert.AreEqual( "close", WindowControlButton.GetMaterialIconName( WindowControlIcon.Close ) );
	}
}
