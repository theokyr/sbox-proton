using Editor;

namespace Tools;

[TestClass]
public class WindowTest
{
	[TestMethod]
	public void EditorMainWindowInitialFlagsStayFrameless()
	{
		var flags = Window.ResolveEditorMainWindowFlags( WindowFlags.Window | WindowFlags.Customized );

		Assert.IsTrue( flags.HasFlag( WindowFlags.Window ) );
		Assert.IsTrue( flags.HasFlag( WindowFlags.FramelessWindowHint ) );
		Assert.IsTrue( flags.HasFlag( WindowFlags.Customized ) );
	}
}
