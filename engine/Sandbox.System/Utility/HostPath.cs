using System.IO;
using System.Runtime.InteropServices;

namespace Sandbox;

/// <summary>
/// Helpers for comparing and storing host filesystem paths. These are not virtual asset paths.
/// </summary>
public static class HostPath
{
	static readonly Lazy<bool> IsWineOrProtonValue = new( DetectWineOrProton );

	public static bool IsWineOrProton => IsWineOrProtonValue.Value;

	public static string Normalize( string path )
	{
		return Normalize( path, IsWineOrProton );
	}

	internal static string Normalize( string path, bool convertWinePaths )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return path;

		path = path.Trim().Trim( '"' ).Replace( '\\', '/' );

		var isWineUnixRootPath = IsWineUnixRootPath( path );
		if ( (convertWinePaths || isWineUnixRootPath) && isWineUnixRootPath )
		{
			path = path.Length == 2 ? "/" : path[2..];
			if ( path.Length == 0 || path[0] != '/' )
				path = "/" + path;
		}

		while ( path.Length > 1 && path.EndsWith( "/", StringComparison.Ordinal ) && !IsDriveRoot( path ) )
		{
			path = path[..^1];
		}

		return path;
	}

	public static string GetFullPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) )
			return path;

		path = Normalize( path );

		if ( IsUnixRootPath( path ) )
			return path;

		return Normalize( Path.GetFullPath( path ) );
	}

	public static string ToWinePath( string path )
	{
		path = Normalize( path );

		if ( string.IsNullOrWhiteSpace( path ) )
			return path;

		if ( TryGetWineDrivePath( path, GetWinePrefixPath(), out var winePath ) )
			return winePath;

		if ( IsUnixRootPath( path ) )
			return $"Z:{path}";

		return path;
	}

	public static string GetManagedFilePath( string path )
	{
		return GetManagedFilePath( path, GetWinePrefixPath(), IsWineOrProton );
	}

	internal static string GetManagedFilePath( string path, string winePrefixOrCompatDataPath )
	{
		return GetManagedFilePath( path, winePrefixOrCompatDataPath, HasWinePrefix( winePrefixOrCompatDataPath ) );
	}

	static string GetManagedFilePath( string path, string winePrefixOrCompatDataPath, bool convertWinePaths )
	{
		path = Normalize( path, convertWinePaths );

		if ( string.IsNullOrWhiteSpace( path ) || !convertWinePaths )
			return path;

		if ( IsWineDrivePath( path ) )
			return path;

		if ( TryGetWineDrivePath( path, winePrefixOrCompatDataPath, out var winePath ) )
			return winePath;

		if ( IsUnixRootPath( path ) )
			return $"Z:{path}";

		return path;
	}

	public static IEnumerable<string> GetNativeSearchPaths( string path )
	{
		return GetNativeSearchPaths( path, GetWinePrefixPath() );
	}

	internal static IEnumerable<string> GetNativeSearchPaths( string path, string winePrefixOrCompatDataPath )
	{
		path = Normalize( path );

		if ( string.IsNullOrWhiteSpace( path ) )
			yield break;

		if ( !HasWinePrefix( winePrefixOrCompatDataPath ) )
		{
			yield return path;
			yield break;
		}

		if ( IsWineDrivePath( path ) )
		{
			yield return path;
			yield break;
		}

		if ( TryGetWineDrivePath( path, winePrefixOrCompatDataPath, out var winePath ) )
		{
			yield return winePath;
			yield break;
		}

		if ( IsUnixRootPath( path ) && !HasHiddenUnixPathSegment( path ) )
		{
			yield return $"Z:{path}";
		}
	}

	public static bool TryGetRelativeWithinRoot( string root, string target, out string relativePath )
	{
		return TryGetRelativeWithinRoot( root, target, out relativePath, IsWineOrProton );
	}

	internal static bool TryGetRelativeWithinRoot( string root, string target, out string relativePath, bool convertWinePaths )
	{
		relativePath = null;

		root = Normalize( root, convertWinePaths );
		target = Normalize( target, convertWinePaths );

		if ( string.IsNullOrWhiteSpace( root ) || string.IsNullOrWhiteSpace( target ) )
			return false;

		root = root.TrimEnd( '/' );

		if ( string.Equals( root, target.TrimEnd( '/' ), StringComparison.OrdinalIgnoreCase ) )
		{
			relativePath = "";
			return true;
		}

		var prefix = root + "/";
		if ( !target.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
			return false;

		relativePath = target[prefix.Length..];
		return true;
	}

	static bool DetectWineOrProton()
	{
		if ( !RuntimeInformation.IsOSPlatform( OSPlatform.Windows ) )
			return false;

		if ( !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( "STEAM_COMPAT_DATA_PATH" ) ) )
			return true;

		if ( !string.IsNullOrWhiteSpace( Environment.GetEnvironmentVariable( "WINEPREFIX" ) ) )
			return true;

		try
		{
			if ( NativeLibrary.TryLoad( "ntdll.dll", out var ntdll ) )
			{
				try
				{
					return NativeLibrary.TryGetExport( ntdll, "wine_get_version", out _ );
				}
				finally
				{
					NativeLibrary.Free( ntdll );
				}
			}
		}
		catch
		{
			// Treat detection failure as a normal Windows runtime.
		}

		return false;
	}

	static string GetWinePrefixPath()
	{
		var compatDataPath = Environment.GetEnvironmentVariable( "STEAM_COMPAT_DATA_PATH" );
		if ( !string.IsNullOrWhiteSpace( compatDataPath ) )
			return compatDataPath;

		return Environment.GetEnvironmentVariable( "WINEPREFIX" );
	}

	static bool TryGetWineDrivePath( string path, string winePrefixOrCompatDataPath, out string winePath )
	{
		winePath = null;

		path = Normalize( path, false );
		var winePrefix = GetWinePrefixRoot( winePrefixOrCompatDataPath );
		if ( string.IsNullOrWhiteSpace( path ) || string.IsNullOrWhiteSpace( winePrefix ) )
			return false;

		var driveC = Normalize( Path.Combine( winePrefix, "drive_c" ), false );
		if ( !Directory.Exists( driveC ) )
			return false;

		foreach ( var (wineRoot, aliasPath) in GetWineDriveAliasPaths( winePrefix, driveC ) )
		{
			if ( TryMapWineDriveAliasPath( path, wineRoot, aliasPath, out winePath ) )
				return true;
		}

		return false;
	}

	static string GetWinePrefixRoot( string winePrefixOrCompatDataPath )
	{
		if ( string.IsNullOrWhiteSpace( winePrefixOrCompatDataPath ) )
			return null;

		var path = Normalize( winePrefixOrCompatDataPath, false );
		if ( Directory.Exists( Path.Combine( path, "drive_c" ) ) )
			return path;

		var protonPrefix = Path.Combine( path, "pfx" );
		if ( Directory.Exists( Path.Combine( protonPrefix, "drive_c" ) ) )
			return Normalize( protonPrefix, false );

		return null;
	}

	static bool HasWinePrefix( string winePrefixOrCompatDataPath )
	{
		return !string.IsNullOrWhiteSpace( GetWinePrefixRoot( winePrefixOrCompatDataPath ) );
	}

	static bool HasHiddenUnixPathSegment( string path )
	{
		if ( !IsUnixRootPath( path ) )
			return false;

		foreach ( var segment in path.Split( '/', StringSplitOptions.RemoveEmptyEntries ) )
		{
			if ( segment.Length > 1 && segment[0] == '.' && !segment.Equals( ".sbox", StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	static IEnumerable<(string WineRoot, string AliasPath)> GetWineDriveAliasPaths( string winePrefix, string driveC )
	{
		yield return ("C:", driveC);

		foreach ( var path in EnumerateFileSystemEntries( driveC ) )
		{
			if ( TryGetRelativeWithinRoot( driveC, path, out var relativePath, false ) )
				yield return ($"C:/{relativePath}", path);
		}

		foreach ( var root in new[]
		{
			Path.Combine( driveC, "projects" ),
			Path.Combine( driveC, "Program Files (x86)", "Steam", "steamapps", "common" )
		} )
		{
			foreach ( var path in EnumerateFileSystemEntries( root ) )
			{
				if ( TryGetRelativeWithinRoot( driveC, path, out var relativePath, false ) )
					yield return ($"C:/{relativePath}", path);
			}
		}

		var dosDevices = Path.Combine( winePrefix, "dosdevices" );
		foreach ( var path in EnumerateFileSystemEntries( dosDevices ) )
		{
			var filename = Path.GetFileName( path );
			if ( filename.Length < 2 || !char.IsLetter( filename[0] ) || filename[1] != ':' )
				continue;

			yield return ($"{char.ToUpperInvariant( filename[0] )}:", path);
		}
	}

	static IEnumerable<string> EnumerateFileSystemEntries( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || !Directory.Exists( path ) )
			yield break;

		IEnumerable<string> entries;
		try
		{
			entries = Directory.EnumerateFileSystemEntries( path ).ToArray();
		}
		catch
		{
			yield break;
		}

		foreach ( var entry in entries )
		{
			yield return Normalize( entry, false );
		}
	}

	static bool TryMapWineDriveAliasPath( string path, string wineRoot, string aliasPath, out string winePath )
	{
		winePath = null;

		aliasPath = Normalize( aliasPath, false );
		var aliasTarget = GetFinalTargetPath( aliasPath );

		if ( !TryGetRelativeWithinRoot( aliasTarget, path, out var relativePath, false ) )
			return false;

		winePath = string.IsNullOrWhiteSpace( relativePath )
			? wineRoot
			: $"{wineRoot}/{relativePath}";
		return true;
	}

	static string GetFinalTargetPath( string path )
	{
		try
		{
			var target = Directory.Exists( path )
				? Directory.ResolveLinkTarget( path, true )
				: File.ResolveLinkTarget( path, true );

			if ( target is not null )
				return Normalize( target.FullName, false );
		}
		catch
		{
			// Treat an unresolvable alias as a normal drive_c path.
		}

		return Normalize( path, false );
	}

	static bool IsWineUnixRootPath( string path )
	{
		if ( path.Length < 2 || !char.IsLetter( path[0] ) || path[1] != ':' )
			return false;

		if ( path[0] is 'z' or 'Z' )
			return path.Length == 2 || path[2] == '/';

		if ( path[0] is 'c' or 'C' || path.Length < 3 || path[2] != '/' )
			return false;

		return StartsWithUnixRootSegment( path[2..] );
	}

	static bool StartsWithUnixRootSegment( string path )
	{
		foreach ( var root in new[] { "/home", "/tmp", "/mnt", "/media", "/run", "/var", "/usr", "/etc", "/opt", "/srv", "/root" } )
		{
			if ( string.Equals( path, root, StringComparison.OrdinalIgnoreCase ) )
				return true;

			if ( path.StartsWith( root + "/", StringComparison.OrdinalIgnoreCase ) )
				return true;
		}

		return false;
	}

	static bool IsUnixRootPath( string path )
	{
		return path.Length > 0 && path[0] == '/';
	}

	static bool IsWineDrivePath( string path )
	{
		return path.Length >= 3 && char.IsLetter( path[0] ) && path[1] == ':' && (path[2] == '/' || path[2] == '\\');
	}

	static bool IsDriveRoot( string path )
	{
		return path.Length == 3 && char.IsLetter( path[0] ) && path[1] == ':' && path[2] == '/';
	}
}
