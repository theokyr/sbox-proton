using System;

namespace Editor;

internal class WindowControlButton : Widget
{
	internal static string GetMaterialIconName( WindowControlIcon icon )
	{
		return icon switch
		{
			WindowControlIcon.Minimize => "remove",
			WindowControlIcon.Maximize => "crop_square",
			WindowControlIcon.Restore => "filter_none",
			WindowControlIcon.Close => "close",
			_ => "close"
		};
	}

	private Action _onClick;
	private WindowControlIcon _icon;
	public WindowControlIcon Icon
	{
		get => _icon;
		set
		{
			if ( _icon == value )
				return;

			_icon = value;
			Update();
		}
	}

	public Color HighlightColor { get; set; } = Theme.Text.WithAlpha( 0.1f );

	public WindowControlButton( WindowControlIcon icon, Action onClick = null )
	{
		_onClick = onClick;

		Icon = icon;
		FixedSize = new Vector2( 40, 32 );
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		_onClick?.Invoke();
		e.Accepted = true;
	}

	protected override void OnPaint()
	{
		Paint.ClearPen();

		if ( Paint.HasMouseOver && Enabled )
		{
			Paint.SetBrush( HighlightColor );
			Paint.DrawRect( LocalRect );
		}

		Paint.ClearBrush();
		Paint.SetPen( Theme.Text );

		if ( !Enabled )
			Paint.SetPen( Theme.Text.WithAlpha( 0.5f ) );

		Paint.DrawIcon( LocalRect, GetMaterialIconName( Icon ), 13.0f );
	}
}
