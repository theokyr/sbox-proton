using static Facepunch.Constants;

namespace Facepunch.Steps;

internal static class BuildProton
{
	internal static ExitCode Run( string targetPlatform, string runtimeIdentifier, bool clean = false, bool skipArtifacts = false )
	{
		if ( ValidateBuildTarget( targetPlatform, runtimeIdentifier ) != ExitCode.Success )
			return ExitCode.Failure;

		if ( new WriteVersion().Run() != ExitCode.Success )
			return ExitCode.Failure;

		if ( !skipArtifacts && new DownloadPublicArtifacts( selection: ArtifactSelection.ProtonWindows ).Run() != ExitCode.Success )
			return ExitCode.Failure;

		if ( new InteropGen( skipNative: true ).Run() != ExitCode.Success )
			return ExitCode.Failure;

		if ( new BuildManaged( clean, runtimeIdentifier ).Run() != ExitCode.Success )
			return ExitCode.Failure;

		return ExitCode.Success;
	}

	private static ExitCode ValidateBuildTarget( string targetPlatform, string runtimeIdentifier )
	{
		if ( !string.Equals( targetPlatform, "win64", StringComparison.OrdinalIgnoreCase ) )
		{
			Log.Error( $"Unsupported Proton target platform '{targetPlatform}'. Only 'win64' is supported." );
			return ExitCode.Failure;
		}

		if ( !string.Equals( runtimeIdentifier, "win-x64", StringComparison.OrdinalIgnoreCase ) )
		{
			Log.Error( $"Unsupported Proton runtime '{runtimeIdentifier}'. Only 'win-x64' is supported." );
			return ExitCode.Failure;
		}

		return ExitCode.Success;
	}
}
