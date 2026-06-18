using Sandbox.Engine;
using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace Editor;

public static partial class AssetSystem
{
	internal static CloudAssetDirectory CloudDirectory { get; set; }

	/// <inheritdoc cref="InstallAsync(Package, bool, Action{float}, CancellationToken)"/>
	public static async Task<Asset> InstallAsync( string packageIdent, bool skipIfInstalled = true, Action<float> loading = null, CancellationToken token = default )
	{
		loading?.Invoke( 0.0f );
		var package = await Package.FetchAsync( packageIdent, false, false );
		return await InstallAsync( package, skipIfInstalled, loading, token );
	}

	/// <summary>
	/// Install a cloud package. Will return the primary asset on completion (if it has one)
	/// </summary>
	/// <param name="package"></param>
	/// <param name="skipIfInstalled">Skip downloading the remote package if any version of this package is already installed.</param>
	/// <param name="loading"></param>
	/// <param name="token"></param>
	public static async Task<Asset> InstallAsync( Package package, bool skipIfInstalled = true, Action<float> loading = null, CancellationToken token = default )
	{
		if ( package == null )
			return null;

		if ( package.Revision == null )
			return null;

		if ( !CanCloudInstall( package ) )
			return null;

		if ( !skipIfInstalled || !IsCloudInstalled( package ) )
		{
			// download the manifest
			await package.Revision.DownloadManifestAsync( token );

			await DownloadCloudFiles( package, loading, token );

			foreach ( var file in package.Revision.Manifest.Files )
			{
				var fullPath = FileSystem.Cloud.GetFullPath( file.Path );
				var asset = RegisterFile( fullPath );

				if ( asset is null )
					continue;

				asset.Package = package;

				// update ref version on any assets that depend on this
				ReplaceReferences( asset.GetDependants( false ), package, package );
			}

			CloudDirectory.AddPackage( package );
			EditorEvent.Run( "package.changed.installed", package );
		}

		return GetInstalledPackageAsset( package );
	}

	public static Asset GetInstalledPackageAsset( Package package, AssetType preferredType = null )
	{
		if ( package is null )
			return null;

		var primaryAssetName = package.PrimaryAsset;
		if ( string.IsNullOrWhiteSpace( primaryAssetName ) )
			primaryAssetName = package.GetMeta<string>( "PrimaryAsset" );

		var primaryAsset = FindOrRegisterCloudAsset( primaryAssetName, preferredType );
		if ( primaryAsset is not null )
			return primaryAsset;

		foreach ( var file in GetPackageFiles( package ) )
		{
			var asset = FindOrRegisterCloudAsset( file, preferredType );
			if ( asset is not null )
				return asset;
		}

		foreach ( var file in package.Revision?.Manifest?.Files ?? Enumerable.Empty<ManifestSchema.File>() )
		{
			var asset = FindOrRegisterCloudAsset( file.Path, preferredType );
			if ( asset is not null )
				return asset;
		}

		return null;
	}

	public static string GetInstalledPackageAssetPath( Package package, AssetType preferredType = null )
	{
		if ( package is null )
			return null;

		var asset = GetInstalledPackageAsset( package, preferredType );
		if ( asset is not null )
			return asset.Path;

		if ( preferredType is not null && preferredType != AssetType.Material )
			return null;

		var primaryAssetName = package.PrimaryAsset;
		if ( string.IsNullOrWhiteSpace( primaryAssetName ) )
			primaryAssetName = package.GetMeta<string>( "PrimaryAsset" );

		if ( IsCloudMaterialFile( primaryAssetName ) && FileSystem.Cloud.FileExists( primaryAssetName ) )
			return primaryAssetName.NormalizeFilename( false );

		foreach ( var file in GetPackageFiles( package ) )
		{
			if ( IsCloudMaterialFile( file ) && FileSystem.Cloud.FileExists( file ) )
				return file.NormalizeFilename( false );
		}

		foreach ( var file in package.Revision?.Manifest?.Files ?? Enumerable.Empty<ManifestSchema.File>() )
		{
			if ( IsCloudMaterialFile( file.Path ) && FileSystem.Cloud.FileExists( file.Path ) )
				return file.Path.NormalizeFilename( false );
		}

		var ident = package.Ident;
		if ( !string.IsNullOrWhiteSpace( ident ) )
		{
			foreach ( var file in FileSystem.Cloud.FindFile( "/", $"{ident}.vmat", true ) )
			{
				return file.NormalizeFilename( false );
			}
		}

		return null;
	}

	public static IEnumerable<string> GetInstalledPackageAssetPaths( Package package, AssetType preferredType = null )
	{
		if ( package is null )
			yield break;

		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		var asset = GetInstalledPackageAsset( package, preferredType );
		if ( asset is not null && !string.IsNullOrWhiteSpace( asset.Path ) && seen.Add( asset.Path ) )
			yield return asset.Path;

		if ( preferredType is not null && preferredType != AssetType.Material )
			yield break;

		var primaryAssetName = package.PrimaryAsset;
		if ( string.IsNullOrWhiteSpace( primaryAssetName ) )
			primaryAssetName = package.GetMeta<string>( "PrimaryAsset" );

		if ( IsCloudMaterialFile( primaryAssetName ) && FileSystem.Cloud.FileExists( primaryAssetName ) && seen.Add( primaryAssetName ) )
			yield return primaryAssetName.NormalizeFilename( false );

		foreach ( var file in GetCloudPackageFilePaths( package ) )
		{
			if ( IsCloudMaterialFile( file ) && FileSystem.Cloud.FileExists( file ) && seen.Add( file ) )
				yield return file.NormalizeFilename( false );
		}

		var ident = package.Ident;
		if ( !string.IsNullOrWhiteSpace( ident ) )
		{
			foreach ( var file in FileSystem.Cloud.FindFile( "/", $"{ident}.vmat", true ) )
			{
				if ( seen.Add( file ) )
					yield return file.NormalizeFilename( false );
			}
		}
	}

	public static bool StageCloudPackageInProjectLibrary( Package package )
	{
		if ( package is null || Project.Current is null )
			return false;

		var files = GetCloudPackageFilePaths( package ).ToArray();
		if ( files.Length == 0 )
			return false;

		var root = Project.Current.GetRootPath();
		if ( string.IsNullOrWhiteSpace( root ) )
			return false;

		var libraryRoot = HostPath.Normalize( Path.Combine( root, "Libraries", "CloudAssets" ) );
		var assetsRoot = HostPath.Normalize( Path.Combine( libraryRoot, "Assets" ) );
		var projectPath = Path.Combine( libraryRoot, "CloudAssets.sbproj" );

		Directory.CreateDirectory( assetsRoot );

		if ( !System.IO.File.Exists( projectPath ) )
		{
			System.IO.File.WriteAllText( projectPath, """
			{
			  "Title": "Generated Cloud Assets",
			  "Type": "library",
			  "Org": "local",
			  "Ident": "sbox_cloud_assets"
			}
			""" );
		}

		var copied = 0;
		foreach ( var file in files )
		{
			if ( string.IsNullOrWhiteSpace( file ) || !FileSystem.Cloud.FileExists( file ) )
				continue;

			var source = HostPath.Normalize( FileSystem.Cloud.GetFullPath( file ) );
			var target = HostPath.Normalize( Path.Combine( assetsRoot, file ) );
			var targetFolder = Path.GetDirectoryName( target );

			if ( !string.IsNullOrWhiteSpace( targetFolder ) )
				Directory.CreateDirectory( targetFolder );

			if ( System.IO.File.Exists( target ) )
			{
				var sourceInfo = new FileInfo( source );
				var targetInfo = new FileInfo( target );
				if ( sourceInfo.Length == targetInfo.Length && sourceInfo.LastWriteTimeUtc <= targetInfo.LastWriteTimeUtc )
					continue;
			}

			System.IO.File.Copy( source, target, true );
			copied++;
		}

		EnsureProjectLibraryContentMounted( assetsRoot );

		if ( copied > 0 )
		{
			Log.Info( $"Staged {copied} cloud package files in project library for {package.FullIdent}." );
		}

		return true;
	}

	static HashSet<string> MountedGeneratedLibraryPaths { get; } = new( StringComparer.OrdinalIgnoreCase );

	static void EnsureProjectLibraryContentMounted( string assetsRoot )
	{
		if ( string.IsNullOrWhiteSpace( assetsRoot ) )
			return;

		assetsRoot = HostPath.Normalize( assetsRoot );

		if ( MountedGeneratedLibraryPaths.Add( assetsRoot ) )
		{
			FileSystem.Content.CreateAndMount( assetsRoot );
			FileSystem.Mounted.CreateAndMount( assetsRoot );
			EngineFileSystem.LibraryContent.CreateAndMount( assetsRoot );
		}

		NativeEngine.FullFileSystem.AddProjectPath( "local.sbox_cloud_assets", assetsRoot );
		foreach ( var searchPath in HostPath.GetNativeSearchPaths( assetsRoot ) )
		{
			NativeEngine.EngineGlue.AddSearchPath( searchPath, "GAME", true );
		}
	}

	static IEnumerable<string> GetCloudPackageFilePaths( Package package )
	{
		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );

		foreach ( var path in GetPackageFiles( package ) )
		{
			if ( !string.IsNullOrWhiteSpace( path ) && seen.Add( path ) )
				yield return path;
		}

		foreach ( var file in package.Revision?.Manifest?.Files ?? Enumerable.Empty<ManifestSchema.File>() )
		{
			if ( !string.IsNullOrWhiteSpace( file.Path ) && seen.Add( file.Path ) )
				yield return file.Path;
		}
	}

	private static bool IsCloudMaterialFile( string path )
	{
		return !string.IsNullOrWhiteSpace( path ) && path.EndsWith( ".vmat", StringComparison.OrdinalIgnoreCase );
	}

	private static Asset FindOrRegisterCloudAsset( string path, AssetType preferredType )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return null;

		var asset = FindByPath( path ) ?? FindByPath( FileSystem.Cloud.GetFullPath( path ) );
		if ( asset is not null )
			return preferredType is null || asset.AssetType == preferredType ? asset : null;

		if ( !FileSystem.Cloud.FileExists( path ) )
			return null;

		asset = RegisterFile( FileSystem.Cloud.GetFullPath( path ) );
		if ( asset is null )
			return null;

		if ( preferredType is not null && asset.AssetType != preferredType )
			return null;

		return asset;
	}

	internal static void UninstallPackage( Package package )
	{
		if ( !IsCloudInstalled( package ) )
		{
			return;
		}

		var contents = GetPackageFiles( package );
		CloudDirectory.RemovePackage( package );

		foreach ( var file in contents )
		{
			var fullPath = FileSystem.Cloud.GetFullPath( file );
			var asset = FindByPath( fullPath );

			if ( asset is null || asset.Package != package )
				continue;

			// some files are shared between packages, so check if any other packages need this file
			var dependants = asset.GetDependants( false );
			Package requiredBy = null;
			foreach ( var dependant in dependants )
			{
				if ( dependant.Package is null ) continue;

				if ( !IsCloudInstalled( dependant.Package ) )
					continue;

				requiredBy = dependant.Package;
				break;
			}

			if ( requiredBy == null )
			{
				// no other packages need this, we can delete it
				asset.Delete();
			}
			else
			{
				// update any assets that depend on this to point to the package that remains
				ReplaceReferences( asset.GetDependants( false ), package, requiredBy );

				asset.Package = requiredBy;
			}
		}
	}

	/// <summary>
	/// Replaces any references in <paramref name="assets"/> to <paramref name="fromPackage"/> with a reference to <paramref name="toPackage"/>
	/// (does a replace by ident to avoid having to open the map etc)
	/// </summary>
	private static void ReplaceReferences( IEnumerable<Asset> assets, Package fromPackage, Package toPackage )
	{
		if ( assets.Count() < 1 ) return;

		string newIdent = toPackage?.GetIdent( false, true );
		string projectPath = Project.Current.GetAssetsPath().Replace( '\\', '/' );

		foreach ( var asset in assets )
		{
			// more bs
			if ( !asset.AbsolutePath.StartsWith( projectPath, StringComparison.OrdinalIgnoreCase ) )
				continue;

			if ( !asset.AssetType.IsGameResource )
			{
				var config = asset?.Publishing?.ProjectConfig;
				if ( config is null || config.EditorReferences is null )
					continue;

				int count = config.EditorReferences.RemoveAll( fromPackage.IsNamed );

				if ( count > 0 )
				{
					if ( newIdent != null && !config.EditorReferences.Contains( newIdent ) )
						config.EditorReferences.Add( newIdent );

					config.EditorReferences = config.EditorReferences.OrderBy( x => x ).ToList();
					asset.MetaData.Set( "publish", asset.Publishing );
				}
			}
			else if ( typeof( GameResource ).IsAssignableFrom( asset.AssetType.ResourceType ) && asset.LoadResource() is GameResource gameResource )
			{
				string filename = asset.GetSourceFile( true );
				if ( string.IsNullOrWhiteSpace( filename ) )
					continue;

				var json = System.IO.File.ReadAllText( filename );

				if ( JsonNode.Parse( json ) is not JsonObject jso )
					continue;

				if ( jso["__references"] is not JsonArray references )
					continue;

				var newReferences = references.Select( x => x.ToString() ).ToList();
				int count = newReferences.RemoveAll( fromPackage.IsNamed );

				if ( count > 0 )
				{
					if ( newIdent != null && !newReferences.Contains( newIdent ) )
						newReferences.Add( newIdent );

					jso["__references"] = JsonValue.Create( newReferences.OrderBy( x => x ) );

					gameResource.SaveToDisk( filename, jso.ToJsonString( Json.options ) );
				}
			}
		}
	}

	/// <summary>
	/// Gets the locally installed package revision by ident
	/// </summary>
	public static Package.IRevision GetInstalledRevision( string packageIdent )
	{
		return CloudDirectory.FindPackage( packageIdent )?.Revision;
	}

	/// <summary>
	/// Is this package installed in our cloud directory?
	/// </summary>
	public static bool IsCloudInstalled( string packageIdent )
	{
		return CloudDirectory.FindPackage( packageIdent ) != null;
	}

	/// <summary>
	/// Is a version this package installed in our cloud directory?
	/// </summary>
	public static bool IsCloudInstalled( Package package, bool exactVersion = false )
	{
		if ( package is null ) return false;
		if ( package.Org is null )
		{
			Log.Trace( $"{package.FullIdent} no org" );
			return false;
		}
		if ( !package.IsRemote )
		{
			Log.Trace( $"{package.FullIdent} not remote" );
			return false;
		}

		var local = CloudDirectory.FindPackage( package.FullIdent );
		if ( local is null )
		{
			Log.Trace( $"{package.FullIdent} wasn't found in CloudDirectory" );
			return false;
		}

		if ( exactVersion && package.Revision is not null && package.Revision.VersionId != local.Revision.VersionId )
		{
			Log.Trace( $"{package.Revision.VersionId} != {local.Revision.VersionId} version is different" );
			return false; // version is different
		}

		return true;
	}

	/// <summary>
	/// Get all packages in the download cache
	/// </summary>
	public static IReadOnlyCollection<Package> GetInstalledPackages()
	{
		return CloudDirectory.GetPackages();
	}

	/// <summary>
	/// Get all packages, referenced by assets in the current project, in the download cache
	/// </summary>
	public static IReadOnlyCollection<Package> GetReferencedPackages()
	{
		return CloudAsset.GetAssetReferences( true )
			.Select( CloudDirectory.FindPackage )
			.Where( p => p != null ).ToList();
	}

	public static IReadOnlyCollection<string> GetPackageFiles( Package package )
	{
		if ( package is null || CloudDirectory is null )
			return Array.Empty<string>();

		return CloudDirectory.GetPackageFiles( package ).ToList();
	}

	/// <summary>
	/// Is this package type something we can install?
	/// </summary>
	public static bool CanCloudInstall( Package package )
	{
		if ( package.TypeName == "map" ) return false;
		if ( package.TypeName == "collection" ) return false;

		return true;
	}

	/// <summary>
	/// Initialize the files from the
	/// </summary>
	private static async Task DownloadCloudFiles( Package package, Action<float> progress, CancellationToken token )
	{
		long totalSize = package.Revision.Manifest.Files.Sum( x => x.Size );
		long downloaded = 0;

		IToolsDll.Current?.RunEvent( "package.download.start", package, token );

		//
		// Process 16 files at a time
		//
		await package.Revision.Manifest.Files.ForEachTaskAsync( async ( e ) =>
		{
			await DownloadFile( package, e, token );
			downloaded += e.Size;

			float frac = (float)((double)downloaded / (double)totalSize);
			progress?.Invoke( frac );
			IToolsDll.Current?.RunEvent( "package.download.update", package, frac );
		}, 16 );

		IToolsDll.Current?.RunEvent( "package.download.complete", package );
	}

	static async Task DownloadFile( Package package, ManifestSchema.File entry, CancellationToken token )
	{
		ThreadSafe.AssertIsMainThread();

		//
		// Ignore thumbnails
		//
		string path = entry.Path.StartsWith( "/" ) ? entry.Path : $"/{entry.Path}";
		if ( path == $"/{package.FullIdent}/thumb.png" )
			return;

		//
		// Ignore assemblies
		//
		if ( entry.Path.EndsWith( ".dll" ) )
			return;

		//
		// This file exists in core, no need to download it (at this moment)
		//
		if ( EngineFileSystem.CoreContent.FileExists( entry.Path ) )
			return;

		CloudDirectory.AddFile( entry.Path, entry.Crc, entry.Size, package.FullIdent, package.Revision.VersionId );

		//
		// File exists - we should probably check crcs and shit
		//
		if ( FileSystem.Cloud.FileExists( entry.Path ) && FileSystem.Cloud.FileSize( entry.Path ) == entry.Size )
			return;

		var targetPath = System.IO.Path.GetDirectoryName( entry.Path );
		FileSystem.Cloud.CreateDirectory( targetPath );

		var targetFile = FileSystem.Cloud.GetFullPath( entry.Path );

		if ( System.IO.File.Exists( targetFile ) )
		{
			System.IO.File.Delete( targetFile );
		}

		if ( entry.Size == 0 )
		{
			await System.IO.File.WriteAllTextAsync( targetFile, "" );
		}
		else
		{
			var url = $"{entry.Url}";
			await Sandbox.Utility.Web.DownloadFile( url, targetFile, token );
		}
	}
}
