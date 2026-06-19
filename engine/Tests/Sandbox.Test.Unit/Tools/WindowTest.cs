using Editor;

namespace Tools;

[TestClass]
public class WindowTest
{
	[TestMethod]
	public void ProtonFramelessWindowFlagsDropDecorationHints()
	{
		var flags = WindowFlags.Window
			| WindowFlags.Customized
			| WindowFlags.WindowTitle
			| WindowFlags.WindowSystemMenuHint
			| WindowFlags.MinMaxButtons
			| WindowFlags.CloseButton;

		var resolved = Window.ResolveProtonFramelessWindowFlags( flags );

		Assert.AreEqual( WindowFlags.Window | WindowFlags.FramelessWindowHint, resolved );
	}

	[TestMethod]
	public void ProtonFramelessWindowFlagsKeepDialogType()
	{
		var flags = WindowFlags.Dialog
			| WindowFlags.WindowTitle
			| WindowFlags.CloseButton;

		var resolved = Window.ResolveProtonFramelessWindowFlags( flags );

		Assert.AreEqual( WindowFlags.Dialog | WindowFlags.FramelessWindowHint, resolved );
	}
}
