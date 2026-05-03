using Facepunch.Steps;
using static Facepunch.Constants;

namespace Facepunch.Pipelines;

internal class BuildProton
{
	public static Pipeline Create( string targetPlatform, string runtimeIdentifier, bool clean = false, bool skipArtifacts = false )
	{
		var builder = new PipelineBuilder( "Proton Build" );

		builder.AddStep( new ValidateProtonBuildTarget( "Validate Proton Build Target", targetPlatform, runtimeIdentifier ) );
		if ( !skipArtifacts )
		{
			builder.AddStep( new DownloadPublicArtifacts( "Download Windows Public Artifacts", selection: ArtifactSelection.ProtonWindows ) );
		}

		builder.AddStep( new Steps.InteropGen( "Interop Gen", skipNative: true ) );
		builder.AddStep( new BuildManaged( "Build Managed Windows Apphosts", clean, runtimeIdentifier ) );

		return builder.Build();
	}
}

internal class ValidateProtonBuildTarget( string name, string targetPlatform, string runtimeIdentifier ) : Step( name )
{
	protected override ExitCode RunInternal()
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
