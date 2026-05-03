using Microsoft.CodeAnalysis;
using System.IO;
using System.Reflection;

namespace Sandbox;

/// <summary>
/// Loads the framework assemblies from the bin/ref folder and makes 
/// them available globally to every compiler.
/// </summary>
[SkipHotload]
static class FrameworkReferences
{
	public static CaseInsensitiveDictionary<PortableExecutableReference> All { get; } = new();

	static FrameworkReferences()
	{
		LoadExternalReferenceAssemblies();
		LoadEmbeddedResources();
	}

	private static void LoadExternalReferenceAssemblies()
	{
		var searchRoots = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		AddSearchRoot( searchRoots, AppContext.BaseDirectory );
		AddSearchRoot( searchRoots, Directory.GetCurrentDirectory() );
		AddSearchRoot( searchRoots, Path.GetDirectoryName( typeof( FrameworkReferences ).Assembly.Location ) );

		foreach ( var refRoot in searchRoots )
		{
			if ( !Directory.Exists( refRoot ) )
				continue;

			foreach ( var referencePath in Directory.EnumerateFiles( refRoot, "*.dll", SearchOption.TopDirectoryOnly ) )
			{
				var name = Path.GetFileName( referencePath );
				if ( All.ContainsKey( name ) )
					continue;

				All[name] = MetadataReference.CreateFromFile( referencePath );
			}
		}
	}

	private static void AddSearchRoot( HashSet<string> searchRoots, string root )
	{
		if ( string.IsNullOrWhiteSpace( root ) )
			return;

		root = HostPath.Normalize( root );

		searchRoots.Add( Path.Combine( root, "refs" ) );
		searchRoots.Add( Path.Combine( root, "refs", "net10.0" ) );
		searchRoots.Add( Path.Combine( root, "bin", "managed", "refs" ) );
		searchRoots.Add( Path.Combine( root, "bin", "managed", "refs", "net10.0" ) );
	}

	private static void LoadEmbeddedResources()
	{
		var assembly = Assembly.GetExecutingAssembly();
		var resourceNames = assembly.GetManifestResourceNames()
									.Where( name => name.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) )
									.ToArray();

		foreach ( var resourceName in resourceNames )
		{
			using ( var stream = assembly.GetManifestResourceStream( resourceName ) )
			{
				var meta = MetadataReference.CreateFromStream( stream, default, default, resourceName );
				All[resourceName] = meta;
			}
		}
	}

	static Assembly FindLoadedAssembly( string name )
	{
		var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
			.FirstOrDefault( assembly => string.Compare( assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase ) == 0 );
		if ( loadedAssembly != null )
			return loadedAssembly;

		try
		{
			// .NET lazy loads assemblies so we might need to load it now...
			return Assembly.Load( name );
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Find a framework reference by its assembly name
	/// </summary>
	public static PortableExecutableReference FindByName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			throw new ArgumentException( $"cannot be null or empty", nameof( name ) );

		//
		// Find a ref assembly
		//
		if ( All.TryGetValue( $"{name}.dll", out var frameworkReference ) )
		{
			return frameworkReference;
		}

		//
		// Find the assembly in our list of loaded assemblies
		// We should really only do this for things like Sandbox.* ?
		//
		var assembly = FindLoadedAssembly( name );

		if ( assembly == null )
		{
			throw new System.Exception( $"Couldn't find {name}.dll" );
		}

		if ( string.IsNullOrEmpty( assembly.Location ) )
		{
			throw new System.Exception( $"Found assembly {name}.dll ({assembly}) - but can't find PortableExecutableReference" );
		}


		return MetadataReference.CreateFromFile( assembly.Location );
	}
}
