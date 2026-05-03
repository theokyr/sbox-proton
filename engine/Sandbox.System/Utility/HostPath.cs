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

		if ( convertWinePaths && IsWineUnixRootPath( path ) )
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

		if ( IsUnixRootPath( path ) )
			return $"Z:{path}";

		return path;
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

	static bool IsWineUnixRootPath( string path )
	{
		return path.Length >= 2
			&& (path[0] == 'z' || path[0] == 'Z')
			&& path[1] == ':'
			&& (path.Length == 2 || path[2] == '/');
	}

	static bool IsUnixRootPath( string path )
	{
		return path.Length > 0 && path[0] == '/';
	}

	static bool IsDriveRoot( string path )
	{
		return path.Length == 3 && char.IsLetter( path[0] ) && path[1] == ':' && path[2] == '/';
	}
}
