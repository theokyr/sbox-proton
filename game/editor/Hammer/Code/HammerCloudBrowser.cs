namespace Editor.MapEditor;

[Dock( "Hammer", "Cloud Browser", "cloud_download" )]
internal class HammerCloudBrowser : CloudAssetBrowser
{
	public static HammerCloudBrowser Instance { get; private set; }

	public HammerCloudBrowser( Widget parent ) : base( parent, null )
	{
		Instance = this;

		OnPackageHighlight = async ( p ) =>
		{
			if ( p.TypeName == "material" )
			{
				var asset = await AssetSystem.InstallAsync( p.FullIdent );
				asset ??= AssetSystem.GetInstalledPackageAsset( p, AssetType.Material );
				if ( asset is not null )
				{
					Hammer.SetCurrentMaterial( asset );
				}
				else
				{
					var materialPath = AssetSystem.GetInstalledPackageAssetPath( p, AssetType.Material );
					if ( string.IsNullOrWhiteSpace( materialPath ) )
					{
						Log.Warning( $"Couldn't resolve a material asset from cloud package {p.FullIdent}." );
					}
					else
					{
						Log.Warning( $"Resolved cloud material {p.FullIdent} as {materialPath}, but no editor Asset was available for Hammer material selection." );
					}
				}
			}
		};
	}
}
